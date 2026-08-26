using Agent.Session;

namespace MerryBot.WebUI;

/// <summary>定时任务管理端 DTO：Content 保持 object?（string 原样、插件自定义模型序列化为 JSON 对象），
/// ContentIsText 标记前端是否可用文本编辑（string/null 可编辑，其他类型只读展示）。</summary>
public sealed record ClockTaskDto(
    Guid Id,
    string PluginId,
    string SessionId,
    string CronExpression,
    string TimeZoneId,
    object? Content,
    bool ContentIsText,
    bool RunOnce,
    int TimeoutSeconds,
    bool Enabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static ClockTaskDto From(ClockTask task) => new(
        task.Id,
        task.PluginId,
        task.SessionId,
        task.CronExpression,
        task.TimeZoneId,
        task.Content,
        task.Content is null or string,
        task.RunOnce,
        task.TimeoutSeconds,
        task.Enabled,
        task.NextRunAtUtc,
        task.LastRunAtUtc,
        task.CreatedAtUtc);
}

/// <summary>更新请求：ContentProvided=false 时不修改内容；true 且 Content 非空白时以文本替换
/// （空文本无法表达"清空为 null"——ClockUpdateRequest 语义约定 null = 不修改）。</summary>
public sealed record ClockTaskUpdateRequest(
    string PluginId,
    string SessionId,
    Guid TaskId,
    string? CronExpression,
    string? TimeZoneId,
    string? Content,
    bool ContentProvided,
    bool? RunOnce,
    int? TimeoutSeconds,
    bool? Enabled);

public sealed record ClockTaskDeleteRequest(
    string PluginId,
    string SessionId,
    Guid TaskId);

public sealed record ClockLogDto(
    Guid RunId,
    Guid TaskId,
    string PluginId,
    string SessionId,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    ClockRunStatus Status,
    string? Error,
    string? SkipReason,
    string? ResultSummary,
    long? DurationMilliseconds)
{
    public static ClockLogDto From(ClockRunLog log) => new(
        log.RunId,
        log.TaskId,
        log.PluginId,
        log.SessionId,
        log.ScheduledAtUtc,
        log.StartedAtUtc,
        log.FinishedAtUtc,
        log.Status,
        log.Error,
        log.SkipReason,
        log.ResultSummary,
        log.DurationMilliseconds);
}
