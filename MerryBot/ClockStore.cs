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
///
/// 任务读取统一走 BsonDocument 弱类型视图：ClockTask.Content 已放宽为 object?（插件可存自定义模型），
/// 强类型反序列化在插件类型被删除后会抛异常，导致 LoadAllAsync 整体失败、调度器无法启动；
/// 弱类型读取 + 逐字段容错（ToContentModel 降级为 JSON 文本）保证单个坏文档不拖垮整体。
/// 写入继续走强类型集合（mapper 自动为 object 内容附加 _type 元数据，与 PluginData.Value 模式一致）。
/// </summary>
internal sealed class CoreClockStore : IClockStore
{
    private const string SchemaVersionId = "persistence-schema-version";
    /// <summary>v2：任务与日志增加 PluginId 字段（调度器按插件隔离共享）。</summary>
    private const string SchemaVersion = "2";
    /// <summary>v1 存量数据没有 PluginId：此前唯一使用方是 agent 插件，迁移时统一归属 agent。</summary>
    private const string LegacyPluginId = "agent";

    private readonly ILiteCollectionAsync<ClockTaskRecord> tasks;
    /// <summary>clock_tasks 的 BsonDocument 视图（同一物理集合）：Content 容错读取用。</summary>
    private readonly ILiteCollectionAsync<BsonDocument> taskDocs;
    private readonly ILiteCollectionAsync<ClockRunLogRecord> runLogs;
    /// <summary>clock_run_logs 的 BsonDocument 视图：v1→v2 迁移补 PluginId 用。</summary>
    private readonly ILiteCollectionAsync<BsonDocument> runLogDocs;
    private readonly ILiteCollectionAsync<MetaRecord> meta;
    private readonly BsonMapper _mapper;
    private readonly SemaphoreSlim claimLock = new(1, 1);

    public CoreClockStore(PluginDatabaseScope database)
    {
        ArgumentNullException.ThrowIfNull(database);
        tasks = database.GetCollection<ClockTaskRecord>("clock_tasks");
        taskDocs = database.GetCollection<BsonDocument>("clock_tasks");
        runLogs = database.GetCollection<ClockRunLogRecord>("clock_run_logs");
        runLogDocs = database.GetCollection<BsonDocument>("clock_run_logs");
        meta = database.GetCollection<MetaRecord>("meta");
        // 与数据库构造时同一 mapper（带 _type 元数据规则）；未显式提供时回退全局
        _mapper = database.Mapper ?? BsonMapper.Global;
    }

    public async Task EnsureInitializedAsync()
    {
        await tasks.EnsureIndexAsync(item => item.SessionId);
        await tasks.EnsureIndexAsync(item => item.PluginId);
        await tasks.EnsureIndexAsync(item => item.NextRunAtUtc);
        await runLogs.EnsureIndexAsync(item => item.SessionId);
        await runLogs.EnsureIndexAsync(item => item.PluginId);
        await runLogs.EnsureIndexAsync(item => item.TaskId);
        await runLogs.EnsureIndexAsync(item => item.ScheduledAtUtc);

        var version = await meta.FindByIdAsync(SchemaVersionId);
        if (version == null)
        {
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionId, Value = SchemaVersion });
            return;
        }
        if (version.Value == SchemaVersion)
        {
            return;
        }
        if (version.Value == "1")
        {
            await MigrateV1ToV2Async();
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionId, Value = SchemaVersion });
            return;
        }
        throw new InvalidOperationException(
            $"agent 持久化数据库版本不受支持: {version.Value}");
    }

    /// <summary>
    /// v1→v2：任务与执行日志补充 PluginId。存量数据缺失该字段时统一归属 agent 插件
    /// （v1 期间只有 agent 的 Cron 工具集会创建定时任务）。
    /// </summary>
    private async Task MigrateV1ToV2Async()
    {
        foreach (var doc in await taskDocs.FindAllAsync())
        {
            if (!TryGetString(doc, "PluginId", out var pluginId) || string.IsNullOrWhiteSpace(pluginId))
            {
                doc["PluginId"] = LegacyPluginId;
                await taskDocs.UpdateAsync(doc);
            }
        }
        foreach (var doc in await runLogDocs.FindAllAsync())
        {
            if (!TryGetString(doc, "PluginId", out var pluginId) || string.IsNullOrWhiteSpace(pluginId))
            {
                doc["PluginId"] = LegacyPluginId;
                await runLogDocs.UpdateAsync(doc);
            }
        }
    }

    public async Task<IReadOnlyList<ClockTask>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await taskDocs.FindAllAsync()).Select(ToTaskModel).ToList();
    }

    public async Task<IReadOnlyList<ClockTask>> ListAsync(string pluginId, string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await taskDocs.FindAllAsync())
            .Select(ToTaskModel)
            .Where(item => item.PluginId == pluginId && item.SessionId == sessionId)
            .OrderBy(item => item.CreatedAtUtc)
            .ToList();
    }

    public async Task<ClockTask?> GetAsync(string pluginId, string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await taskDocs.FindByIdAsync(taskId);
        if (doc == null)
        {
            return null;
        }
        var task = ToTaskModel(doc);
        return task.PluginId == pluginId && task.SessionId == sessionId ? task : null;
    }

    public async Task CreateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await taskDocs.FindByIdAsync(task.Id) != null)
        {
            throw new InvalidOperationException($"定时任务已存在: {task.Id}");
        }
        await tasks.InsertAsync(ToRecord(task));
    }

    public async Task UpdateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await taskDocs.FindByIdAsync(task.Id) == null)
        {
            throw new KeyNotFoundException($"未找到定时任务: {task.Id}");
        }
        await tasks.UpdateAsync(ToRecord(task));
    }

    public async Task DeleteAsync(string pluginId, string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await taskDocs.FindByIdAsync(taskId);
        if (doc != null &&
            GetString(doc, "PluginId") == pluginId &&
            GetString(doc, "SessionId") == sessionId)
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
            // 弱类型读取：Content 可能是插件自定义模型，类型已删除时强类型读取会抛异常
            var doc = await taskDocs.FindByIdAsync(expectedTask.Id);
            if (doc == null)
            {
                return null;
            }
            var task = ToTaskModel(doc);
            if (task.PluginId != expectedTask.PluginId ||
                task.SessionId != expectedTask.SessionId ||
                !task.Enabled)
            {
                return null;
            }

            // LiteDB 读回的 DateTime 已启用 UTC_DATE pragma（Kind=Utc）；
            // 仍统一转成 UTC 实例再与期望值比较，否则 CAS 永远不相等、领取被静默拒绝。
            var storedNext = task.NextRunAtUtc?.UtcDateTime;
            var expectedNext = expectedTask.NextRunAtUtc?.UtcDateTime;
            if (storedNext != expectedNext ||
                storedNext != ToUtcDateTime(scheduledAtUtc))
            {
                return null;
            }

            task.Enabled = !disableTask;
            task.NextRunAtUtc = nextRunAtUtc;
            task.LastRunAtUtc = scheduledAtUtc;
            task.UpdatedAtUtc = startedAtUtc;
            await tasks.UpdateAsync(ToRecord(task));

            var log = new ClockRunLogRecord
            {
                RunId = Guid.NewGuid(),
                TaskId = task.Id,
                PluginId = task.PluginId,
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
        string pluginId,
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 100);
        return (await runLogs.FindAllAsync())
            .Where(item => item.PluginId == pluginId)
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
        PluginId = task.PluginId,
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

    /// <summary>BsonDocument（弱类型）→ ClockTask 模型：逐字段容错读取。</summary>
    private ClockTask ToTaskModel(BsonDocument doc) => new()
    {
        Id = doc["_id"].IsGuid ? doc["_id"].AsGuid : Guid.Empty,
        PluginId = GetString(doc, "PluginId"),
        SessionId = GetString(doc, "SessionId"),
        CronExpression = GetString(doc, "CronExpression"),
        TimeZoneId = GetString(doc, "TimeZoneId"),
        Content = ToContentModel(doc.TryGetValue("Content", out var content) ? content : null),
        Trigger = new ClockTrigger
        {
            Type = GetString(doc, "TriggerType"),
            Id = GetString(doc, "TriggerId"),
        },
        RunOnce = GetBool(doc, "RunOnce"),
        TimeoutSeconds = GetInt(doc, "TimeoutSeconds"),
        Enabled = GetBool(doc, "Enabled"),
        NextRunAtUtc = GetNullableDateTime(doc, "NextRunAtUtc"),
        LastRunAtUtc = GetNullableDateTime(doc, "LastRunAtUtc"),
        CreatedAtUtc = GetDateTime(doc, "CreatedAtUtc"),
        UpdatedAtUtc = GetDateTime(doc, "UpdatedAtUtc"),
    };

    /// <summary>
    /// Content 容错还原：null→null、string→string、带 _type 的文档→插件 POCO；
    /// 插件类型已不存在时降级为原始 JSON 文本（任务仍可加载/列出/删除，下次保存时固化为文本）。
    /// </summary>
    private object? ToContentModel(BsonValue? value)
    {
        if (value is null || value.IsNull)
        {
            return null;
        }
        if (value.IsString)
        {
            return value.AsString;
        }
        try
        {
            return _mapper.Deserialize<object>(value);
        }
        catch (Exception)
        {
            // 类型已删除（插件被移除）：降级为 JSON 文本
            return value.ToString();
        }
    }

    private static ClockRunLogRecord ToRecord(ClockRunLog log) => new()
    {
        RunId = log.RunId,
        TaskId = log.TaskId,
        PluginId = log.PluginId,
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
        PluginId = log.PluginId,
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
        // 连接已启用 UTC_DATE pragma（HistoryRecorder/PluginStorageDatabase 构造时设置）：
        // LiteDB 读取返回 Kind=Utc 的实例，这里直接原样转 DateTimeOffset。
        // 保留 ToUniversalTime() 作为安全网：若某处回退到未启用 pragma 的连接，
        // 读到 Kind=Local 墙钟时仍能归一化为 UTC，避免偏移一个本地时区（如 +8 小时）。
        return new DateTimeOffset(value.ToUniversalTime());
    }

    private static DateTimeOffset? ToNullableDateTimeOffset(DateTime? value) =>
        value is { } timestamp ? ToDateTimeOffset(timestamp) : null;

    // ── BsonDocument 字段容错读取辅助 ──────────────────────────────────────

    private static string GetString(BsonDocument doc, string field) =>
        TryGetString(doc, field, out var value) ? value : string.Empty;

    private static bool TryGetString(BsonDocument doc, string field, out string value)
    {
        if (doc.TryGetValue(field, out var raw) && raw.IsString)
        {
            value = raw.AsString;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool GetBool(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && value.IsBoolean && value.AsBoolean;

    private static int GetInt(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && value.IsNumber ? value.AsInt32 : 0;

    private static DateTimeOffset? GetNullableDateTime(BsonDocument doc, string field)
    {
        if (!doc.TryGetValue(field, out var value) || !value.IsDateTime)
        {
            return null;
        }
        return ToDateTimeOffset(value.AsDateTime);
    }

    private static DateTimeOffset GetDateTime(BsonDocument doc, string field)
    {
        if (doc.TryGetValue(field, out var value) && value.IsDateTime)
        {
            return ToDateTimeOffset(value.AsDateTime);
        }
        return DateTimeOffset.MinValue;
    }

    private sealed class ClockTaskRecord
    {
        [BsonId] public Guid Id { get; set; }
        public string PluginId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CronExpression { get; set; } = string.Empty;
        public string TimeZoneId { get; set; } = "Asia/Shanghai";
        /// <summary>任务内容：可为 null 或插件自定义模型（mapper 自动附加 _type 元数据）。</summary>
        public object? Content { get; set; }
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
        public string PluginId { get; set; } = string.Empty;
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
