using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmBackend;

namespace Agent.Session;

/// <summary>
/// Session-scoped facade for the shared ClockService.
/// The scheduler itself is shared by all sessions; this tool set only applies
/// the current session's ownership boundary to CRUD and log queries.
/// </summary>
public sealed class Cron : ToolSet
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _sessionId;
    private readonly ClockService _service;
    private readonly ToolSetBridge _bridge;

    public Cron(string sessionId, ClockService service)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }
        _sessionId = sessionId;
        _service = service ?? throw new ArgumentNullException(nameof(service));

        var builder = new ToolSetBridge.Builder(BuildPrompt());
        builder.AddFunction<ClockCreateArgs>(
            "clock_create",
            "创建定时任务。cron 使用 Linux 五字段格式：分 时 日 月 周；run_once=true 时只执行下一次匹配。",
            CreateAsync);
        builder.AddFunction<ClockListArgs>(
            "clock_list",
            "列出当前会话的定时任务摘要。",
            ListAsync);
        builder.AddFunction<ClockGetArgs>(
            "clock_get",
            "按 ID 查看当前会话定时任务的完整详情。",
            GetAsync);
        builder.AddFunction<ClockUpdateArgs>(
            "clock_update",
            "按 ID 更新当前会话定时任务；未传入的字段保持不变。",
            UpdateAsync);
        builder.AddFunction<ClockDeleteArgs>(
            "clock_delete",
            "按 ID 删除当前会话定时任务，执行历史会保留。",
            DeleteAsync);
        builder.AddFunction<ClockLogArgs>(
            "clock_log",
            "查看当前会话的定时任务执行记录，可按任务 ID、状态和时间范围过滤。",
            LogAsync);
        _bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => _bridge.Tools();

    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
        => _bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);

    public override string? Prompt() =>
        "clock工具属于当前会话；cron 使用 Linux 五字段格式（分 时 日 月 周），默认时区为 Asia/Shanghai，默认超时为 600 秒。";

    private async Task<string> CreateAsync(ClockCreateArgs args)
    {
        var task = await _service.CreateAsync(_sessionId, new ClockCreateRequest
        {
            CronExpression = args.cron,
            TimeZoneId = args.timezone,
            Content = args.content,
            Trigger = args.trigger,
            RunOnce = args.run_once,
            TimeoutSeconds = args.timeout_seconds,
        });
        return Serialize(task);
    }

    private async Task<string> ListAsync(ClockListArgs _)
    {
        var tasks = await _service.ListAsync(_sessionId);
        return Serialize(tasks.Select(ToSummary).ToList());
    }

    private async Task<string> GetAsync(ClockGetArgs args)
    {
        var task = await _service.GetAsync(_sessionId, args.id);
        return Serialize(task);
    }

    private async Task<string> UpdateAsync(ClockUpdateArgs args)
    {
        var task = await _service.UpdateAsync(_sessionId, args.id, new ClockUpdateRequest
        {
            CronExpression = args.cron,
            TimeZoneId = args.timezone,
            Content = args.content,
            Trigger = args.trigger,
            RunOnce = args.run_once,
            TimeoutSeconds = args.timeout_seconds,
            Enabled = args.enabled,
        });
        return Serialize(task);
    }

    private async Task<string> DeleteAsync(ClockDeleteArgs args)
    {
        await _service.DeleteAsync(_sessionId, args.id);
        return Serialize(new { id = args.id, deleted = true });
    }

    private async Task<string> LogAsync(ClockLogArgs args)
    {
        var logs = await _service.QueryLogsAsync(_sessionId, new ClockLogQuery
        {
            TaskId = args.task_id,
            Status = ParseStatus(args.status),
            FromUtc = args.from,
            ToUtc = args.to,
            Limit = args.limit ?? 20,
        });
        return Serialize(logs);
    }

    private static ClockRunStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (Enum.TryParse<ClockRunStatus>(value, ignoreCase: true, out var status))
        {
            return status;
        }
        throw new ArgumentException($"未知执行状态: {value}");
    }

    private static object ToSummary(ClockTask task) => new
    {
        id = task.Id,
        cron = task.CronExpression,
        timezone = task.TimeZoneId,
        content = task.Content.Length <= 120 ? task.Content : task.Content[..120] + "…",
        trigger = task.Trigger,
        runOnce = task.RunOnce,
        timeoutSeconds = task.TimeoutSeconds,
        enabled = task.Enabled,
        nextRunAtUtc = task.NextRunAtUtc,
        lastRunAtUtc = task.LastRunAtUtc,
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string BuildPrompt() =>
        "clock_update 只修改实际传入的字段；clock_log 的状态可使用 running、succeeded、timedOut、failed、skipped、cancelled。";

    private sealed class ClockCreateArgs
    {
        [Description("Linux 五字段 Cron 表达式：分 时 日 月 周，例如 0 9 * * 1-5")]
        public string cron { get; set; } = string.Empty;

        [Description("任务执行时发送给 Agent 的内容")]
        public string content { get; set; } = string.Empty;

        [Description("触发对象，例如 { type: 'group', id: '123456' }")]
        public ClockTrigger trigger { get; set; } = new();

        [Description("时区，默认 Asia/Shanghai")]
        public string? timezone { get; set; }

        [Description("是否只执行下一次匹配，默认 false")]
        public bool? run_once { get; set; }

        [Description("超时秒数，默认 600，范围 1-86400")]
        public int? timeout_seconds { get; set; }
    }

    private sealed class ClockListArgs
    {
    }

    private sealed class ClockGetArgs
    {
        [Description("任务 ID")]
        public Guid id { get; set; }
    }

    private sealed class ClockUpdateArgs
    {
        [Description("任务 ID")]
        public Guid id { get; set; }

        [Description("新的 Linux 五字段 Cron 表达式")]
        public string? cron { get; set; }

        [Description("新的任务内容")]
        public string? content { get; set; }

        [Description("新的触发对象")]
        public ClockTrigger? trigger { get; set; }

        [Description("新的时区")]
        public string? timezone { get; set; }

        [Description("是否只执行下一次匹配")]
        public bool? run_once { get; set; }

        [Description("新的超时秒数")]
        public int? timeout_seconds { get; set; }

        [Description("是否启用")]
        public bool? enabled { get; set; }
    }

    private sealed class ClockDeleteArgs
    {
        [Description("任务 ID")]
        public Guid id { get; set; }
    }

    private sealed class ClockLogArgs
    {
        [Description("可选，按任务 ID 过滤")]
        public Guid? task_id { get; set; }

        [Description("可选，running/succeeded/timedOut/failed/skipped/cancelled")]
        public string? status { get; set; }

        [Description("可选，开始时间，ISO 8601 UTC")]
        public DateTimeOffset? from { get; set; }

        [Description("可选，结束时间，ISO 8601 UTC")]
        public DateTimeOffset? to { get; set; }

        [Description("返回条数，默认 20，最大 100")]
        public int? limit { get; set; }
    }
}
