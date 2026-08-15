using Agent.Session;
using DataProvider;
using LiteDB;
using LiteDB.Async;

namespace MerryBot;

/// <summary>
/// LiteDB-backed scheduler storage owned by core，使用 core 命名空间（scope "core"），与插件数据隔离；
/// 不兼容旧版 agent 插件 scope 下已存在的定时任务数据（无需迁移）。
/// claimLock 仅为进程内互斥；多实例部署（多个机器人进程共享同一数据库）时，需要外部互斥
/// （如分布式锁）保证同一任务不会同时被多个实例领取执行。
/// </summary>
internal sealed class CoreClockStore : IClockStore
{
    private const string SchemaVersionId = "persistence-schema-version";
    private const string SchemaVersion = "1";

    private readonly ILiteCollectionAsync<ClockTaskRecord> tasks;
    private readonly ILiteCollectionAsync<ClockRunLogRecord> runLogs;
    private readonly ILiteCollectionAsync<MetaRecord> meta;
    private readonly SemaphoreSlim claimLock = new(1, 1);

    public CoreClockStore(PluginDatabaseScope database)
    {
        ArgumentNullException.ThrowIfNull(database);
        tasks = database.GetCollection<ClockTaskRecord>("clock_tasks");
        runLogs = database.GetCollection<ClockRunLogRecord>("clock_run_logs");
        meta = database.GetCollection<MetaRecord>("meta");
    }

    public async Task EnsureInitializedAsync()
    {
        await tasks.EnsureIndexAsync(item => item.SessionId);
        await tasks.EnsureIndexAsync(item => item.NextRunAtUtc);
        await runLogs.EnsureIndexAsync(item => item.SessionId);
        await runLogs.EnsureIndexAsync(item => item.TaskId);
        await runLogs.EnsureIndexAsync(item => item.ScheduledAtUtc);

        var version = await meta.FindByIdAsync(SchemaVersionId);
        if (version == null)
        {
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionId, Value = SchemaVersion });
            return;
        }
        if (version.Value != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"agent 持久化数据库版本不受支持: {version.Value}");
        }
    }

    public async Task<IReadOnlyList<ClockTask>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await tasks.FindAllAsync()).Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ClockTask>> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await tasks.FindAllAsync())
            .Where(item => item.SessionId == sessionId)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(ToModel)
            .ToList();
    }

    public async Task<ClockTask?> GetAsync(string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await tasks.FindByIdAsync(taskId);
        return task?.SessionId == sessionId ? ToModel(task) : null;
    }

    public async Task CreateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await tasks.FindByIdAsync(task.Id) != null)
        {
            throw new InvalidOperationException($"定时任务已存在: {task.Id}");
        }
        await tasks.InsertAsync(ToRecord(task));
    }

    public async Task UpdateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await tasks.FindByIdAsync(task.Id) == null)
        {
            throw new KeyNotFoundException($"未找到定时任务: {task.Id}");
        }
        await tasks.UpdateAsync(ToRecord(task));
    }

    public async Task DeleteAsync(string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await tasks.FindByIdAsync(taskId);
        if (task?.SessionId == sessionId)
        {
            await tasks.DeleteAsync(taskId);
        }
    }

    public async Task<ClockRunLog?> TryClaimAsync(
        ClockTask expectedTask,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? nextRunAtUtc,
        bool disableTask,
        CancellationToken cancellationToken = default)
    {
        await claimLock.WaitAsync(cancellationToken);
        try
        {
            var task = await tasks.FindByIdAsync(expectedTask.Id);
            if (task == null ||
                task.SessionId != expectedTask.SessionId ||
                !task.Enabled)
            {
                return null;
            }

            // LiteDB 读回的 DateTime 是本地墙钟（Kind=Local，刻度带本地偏移），
            // 必须统一转成 UTC 实例再与期望值比较，否则 CAS 永远不相等、领取被静默拒绝。
            var storedNext = task.NextRunAtUtc?.ToUniversalTime();
            var expectedNext = expectedTask.NextRunAtUtc?.UtcDateTime;
            if (storedNext != expectedNext ||
                storedNext != ToUtcDateTime(scheduledAtUtc))
            {
                return null;
            }

            task.Enabled = !disableTask;
            task.NextRunAtUtc = ToNullableUtcDateTime(nextRunAtUtc);
            task.LastRunAtUtc = ToUtcDateTime(scheduledAtUtc);
            task.UpdatedAtUtc = ToUtcDateTime(startedAtUtc);
            await tasks.UpdateAsync(task);

            var log = new ClockRunLogRecord
            {
                RunId = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = task.SessionId,
                ScheduledAtUtc = ToUtcDateTime(scheduledAtUtc),
                StartedAtUtc = ToUtcDateTime(startedAtUtc),
                Status = ClockRunStatus.Running,
            };
            await runLogs.InsertAsync(log);
            return ToModel(log);
        }
        finally
        {
            claimLock.Release();
        }
    }

    public async Task AppendRunLogAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await runLogs.UpsertAsync(ToRecord(log));
    }

    public async Task CompleteRunAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await runLogs.FindByIdAsync(log.RunId) == null)
        {
            throw new KeyNotFoundException($"未找到执行记录: {log.RunId}");
        }
        await runLogs.UpdateAsync(ToRecord(log));
    }

    public async Task RecoverInterruptedRunsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recoveredAt = ToUtcDateTime(recoveredAtUtc);
        var interrupted = (await runLogs.FindAllAsync())
            .Where(item => item.Status == ClockRunStatus.Running)
            .ToList();
        foreach (var log in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Status = ClockRunStatus.Cancelled;
            log.FinishedAtUtc = recoveredAt;
            log.Error = "服务重启前执行被中断";
            await runLogs.UpdateAsync(log);
        }
    }

    public async Task<IReadOnlyList<ClockRunLog>> QueryLogsAsync(
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 100);
        return (await runLogs.FindAllAsync())
            .Where(item => item.SessionId == sessionId)
            .Where(item => query.TaskId == null || item.TaskId == query.TaskId)
            .Where(item => query.Status == null || item.Status == query.Status)
            .Where(item => query.FromUtc == null || item.ScheduledAtUtc.ToUniversalTime() >= ToUtcDateTime(query.FromUtc.Value))
            .Where(item => query.ToUtc == null || item.ScheduledAtUtc.ToUniversalTime() <= ToUtcDateTime(query.ToUtc.Value))
            .OrderByDescending(item => item.ScheduledAtUtc)
            .Take(limit)
            .Select(ToModel)
            .ToList();
    }

    private static ClockTaskRecord ToRecord(ClockTask task) => new()
    {
        Id = task.Id,
        SessionId = task.SessionId,
        CronExpression = task.CronExpression,
        TimeZoneId = task.TimeZoneId,
        Content = task.Content,
        TriggerType = task.Trigger.Type,
        TriggerId = task.Trigger.Id,
        RunOnce = task.RunOnce,
        TimeoutSeconds = task.TimeoutSeconds,
        Enabled = task.Enabled,
        NextRunAtUtc = ToNullableUtcDateTime(task.NextRunAtUtc),
        LastRunAtUtc = ToNullableUtcDateTime(task.LastRunAtUtc),
        CreatedAtUtc = ToUtcDateTime(task.CreatedAtUtc),
        UpdatedAtUtc = ToUtcDateTime(task.UpdatedAtUtc),
    };

    private static ClockTask ToModel(ClockTaskRecord task) => new()
    {
        Id = task.Id,
        SessionId = task.SessionId,
        CronExpression = task.CronExpression,
        TimeZoneId = task.TimeZoneId,
        Content = task.Content,
        Trigger = new ClockTrigger { Type = task.TriggerType, Id = task.TriggerId },
        RunOnce = task.RunOnce,
        TimeoutSeconds = task.TimeoutSeconds,
        Enabled = task.Enabled,
        NextRunAtUtc = ToNullableDateTimeOffset(task.NextRunAtUtc),
        LastRunAtUtc = ToNullableDateTimeOffset(task.LastRunAtUtc),
        CreatedAtUtc = ToDateTimeOffset(task.CreatedAtUtc),
        UpdatedAtUtc = ToDateTimeOffset(task.UpdatedAtUtc),
    };

    private static ClockRunLogRecord ToRecord(ClockRunLog log) => new()
    {
        RunId = log.RunId,
        TaskId = log.TaskId,
        SessionId = log.SessionId,
        ScheduledAtUtc = ToUtcDateTime(log.ScheduledAtUtc),
        StartedAtUtc = ToNullableUtcDateTime(log.StartedAtUtc),
        FinishedAtUtc = ToNullableUtcDateTime(log.FinishedAtUtc),
        Status = log.Status,
        Error = log.Error,
        SkipReason = log.SkipReason,
        ResultSummary = log.ResultSummary,
    };

    private static ClockRunLog ToModel(ClockRunLogRecord log) => new()
    {
        RunId = log.RunId,
        TaskId = log.TaskId,
        SessionId = log.SessionId,
        ScheduledAtUtc = ToDateTimeOffset(log.ScheduledAtUtc),
        StartedAtUtc = ToNullableDateTimeOffset(log.StartedAtUtc),
        FinishedAtUtc = ToNullableDateTimeOffset(log.FinishedAtUtc),
        Status = log.Status,
        Error = log.Error,
        SkipReason = log.SkipReason,
        ResultSummary = log.ResultSummary,
    };

    private static DateTime ToUtcDateTime(DateTimeOffset value) => value.UtcDateTime;

    private static DateTime? ToNullableUtcDateTime(DateTimeOffset? value) =>
        value?.UtcDateTime;

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        // LiteDB 读回的 DateTime 是本地墙钟（Kind=Local，刻度已含本地偏移）：
        // 必须转成 UTC 实例，否则下游拿到的时间比真实时刻偏移一个本地时区（如 +8 小时）。
        return new DateTimeOffset(value.ToUniversalTime());
    }

    private static DateTimeOffset? ToNullableDateTimeOffset(DateTime? value) =>
        value is { } timestamp ? ToDateTimeOffset(timestamp) : null;

    private sealed class ClockTaskRecord
    {
        [BsonId] public Guid Id { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string CronExpression { get; set; } = string.Empty;
        public string TimeZoneId { get; set; } = "Asia/Shanghai";
        public string Content { get; set; } = string.Empty;
        public string TriggerType { get; set; } = string.Empty;
        public string TriggerId { get; set; } = string.Empty;
        public bool RunOnce { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool Enabled { get; set; }
        public DateTime? NextRunAtUtc { get; set; }
        public DateTime? LastRunAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class ClockRunLogRecord
    {
        [BsonId] public Guid RunId { get; set; }
        public Guid TaskId { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public DateTime ScheduledAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public ClockRunStatus Status { get; set; }
        public string? Error { get; set; }
        public string? SkipReason { get; set; }
        public string? ResultSummary { get; set; }
    }

    private sealed class MetaRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
