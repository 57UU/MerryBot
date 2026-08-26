using System.Text.Json.Serialization;
using Cronos;

namespace Agent.Session;

public sealed class ClockTask
{
    public Guid Id { get; set; }
    /// <summary>任务归属的插件 Id：CRUD 与执行器路由的所有权边界（与 SessionId 共同限定可见范围）。</summary>
    public string PluginId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;

    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    /// <summary>任务内容：可为 null（插件不需要内容）或插件自定义模型（存储层弱类型读取，类型已删除时降级为 JSON 文本）。</summary>
    public object? Content { get; set; }
    public ClockTrigger Trigger { get; set; } = new();

    public bool RunOnce { get; set; }
    public int TimeoutSeconds { get; set; } = 600;
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? NextRunAtUtc { get; set; }
    public DateTimeOffset? LastRunAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>缓存的 Cron 解析结果，避免每次调度重复解析；由 ClockService 维护，表达式变更时失效。</summary>
    [JsonIgnore]
    public CronExpression? ParsedCron { get; set; }

    public ClockTask Clone()
    {
        return new ClockTask
        {
            Id = Id,
            PluginId = PluginId,
            SessionId = SessionId,
            CronExpression = CronExpression,
            TimeZoneId = TimeZoneId,
            Content = Content,
            Trigger = Trigger.Clone(),
            RunOnce = RunOnce,
            TimeoutSeconds = TimeoutSeconds,
            Enabled = Enabled,
            NextRunAtUtc = NextRunAtUtc,
            LastRunAtUtc = LastRunAtUtc,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            ParsedCron = ParsedCron,
        };
    }
}

public sealed class ClockTrigger
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;

    public ClockTrigger Clone() => new()
    {
        Type = Type,
        Id = Id,
    };
}

public enum ClockRunStatus
{
    Running,
    Succeeded,
    TimedOut,
    Failed,
    Skipped,
    Cancelled,
}

public sealed class ClockRunLog
{
    public Guid RunId { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>冗余存储任务归属插件 Id，供跨插件管理端（WebUI）按插件过滤日志。</summary>
    public string PluginId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;

    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public ClockRunStatus Status { get; set; }

    public string? Error { get; set; }
    public string? SkipReason { get; set; }
    public string? ResultSummary { get; set; }

    public long? DurationMilliseconds => StartedAtUtc is { } started && FinishedAtUtc is { } finished
        ? Math.Max(0, (long)(finished - started).TotalMilliseconds)
        : null;

    public ClockRunLog Clone() => new()
    {
        RunId = RunId,
        TaskId = TaskId,
        PluginId = PluginId,
        SessionId = SessionId,
        ScheduledAtUtc = ScheduledAtUtc,
        StartedAtUtc = StartedAtUtc,
        FinishedAtUtc = FinishedAtUtc,
        Status = Status,
        Error = Error,
        SkipReason = SkipReason,
        ResultSummary = ResultSummary,
    };
}

public sealed class ClockLogQuery
{
    public Guid? TaskId { get; init; }
    public ClockRunStatus? Status { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Limit { get; init; } = 20;
}

public sealed class ClockCreateRequest
{
    public string CronExpression { get; init; } = string.Empty;
    public string? TimeZoneId { get; init; }
    /// <summary>任务内容：可为 null 或插件自定义模型（agent 场景为字符串提示词）。</summary>
    public object? Content { get; init; }
    public ClockTrigger Trigger { get; init; } = new();
    public bool? RunOnce { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class ClockUpdateRequest
{
    public string? CronExpression { get; init; }
    public string? TimeZoneId { get; init; }
    /// <summary>任务内容：null 表示未修改（语义与 LiteDB 判空一致），插件可传入自己的模型。</summary>
    public object? Content { get; init; }
    public ClockTrigger? Trigger { get; init; }
    public bool? RunOnce { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool? Enabled { get; init; }
}

public sealed class ClockExecutionResult
{
    public bool Succeeded { get; init; }
    public string? ResultSummary { get; init; }
    public string? Error { get; init; }

    public static ClockExecutionResult Success(string? resultSummary = null) => new()
    {
        Succeeded = true,
        ResultSummary = resultSummary,
    };

    public static ClockExecutionResult Failure(string error) => new()
    {
        Succeeded = false,
        Error = error,
    };
}
