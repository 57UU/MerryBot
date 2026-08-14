using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LlmBackend;

namespace Agent.Session;

/// <summary>
/// 终端工具集：注册 bash / task_list / task_output / task_stop 四个工具，效果对齐内置 Bash 工具。
/// bash 进程懒加载——构造时不启动，首次前台调用时才创建共享常驻终端，之后跨调用保留 shell 状态（如 cd）。
/// user 构造参数非空时以 sudo -u user 运行 bash；后台任务各自使用独立终端实例。
/// 后台任务完成时通过 sessionManager 主动通知所属 session 的 Agent（stackable 类型，避免积压）。
/// </summary>
public class TerminalToolSet : ToolSet, IDisposable
{
    /// <summary>前台命令默认超时（秒）</summary>
    public const int DefaultSyncTimeoutSeconds = 60;
    /// <summary>后台命令默认超时（秒）</summary>
    public const int DefaultBackgroundTimeoutSeconds = 600;
    /// <summary>后台任务最大存活时间，超龄任务在查询时顺带清理，防泄漏</summary>
    private static readonly TimeSpan MaxTaskAge = TimeSpan.FromMinutes(5);
    /// <summary>后台任务完成通知的结果摘要长度限制（字符）</summary>
    private const int NotifyResultLimit = 2000;

    private readonly ToolSetBridge bridge;
    private readonly AgentSessionManager _sessionManager;
    private readonly string _sessionId;
    private readonly string? user;
    private readonly Lazy<Terminal> _sync;
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();

    private sealed record BackgroundTask(string Id, string Description, Terminal Terminal, Task<string> Task, DateTime StartTime);

    public TerminalToolSet(AgentSessionManager sessionManager, string sessionId, string? user = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sessionId = sessionId;
        this.user = user;
        if (!string.IsNullOrEmpty(user) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("user（sudo 模式）仅支持 Linux");
        }
        // 懒加载：首次前台调用取 .Value 时才启动共享 bash 进程
        _sync = new Lazy<Terminal>(() => Terminal.Create(user));

        var builder = new ToolSetBridge.Builder(
            "如需执行命令，调用 bash 工具；命令在常驻 shell 中执行，cd 等状态跨调用保留；长任务可后台执行，用 task_list / task_output / task_stop 管理。");
        builder.AddFunction<BashArgs>("bash",
            "执行 bash 命令并返回输出。前台（默认）：在共享常驻 shell 中串行执行，同步返回输出，默认超时 60 秒，超时后终止并重启 shell；" +
            "run_in_background=true 时后台执行，立即返回 task_id，之后用 task_output 查询结果，默认超时 600 秒，disable_timeout=true 则不设超时。",
            RunAsync);
        builder.AddFunction<TaskListArgs>("task_list",
            "列出所有后台 bash 任务：id、说明、运行中/已完成、已耗时。",
            _ => Task.FromResult(ListTasks()));
        builder.AddFunction<TaskOutputArgs>("task_output",
            "查询后台 bash 任务结果：未完成返回执行中提示，已完成返回结果并移除任务。",
            QueryTaskAsync);
        builder.AddFunction<TaskStopArgs>("task_stop",
            "终止指定后台 bash 任务。",
            StopTaskAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall) => bridge.InvokeAsync(cancellationToken, toolCall);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：bash</summary>
    private sealed class BashArgs
    {
        [Description("要执行的命令")]
        public string command { get; set; } = string.Empty;

        [Description("工作目录，如 /tmp")]
        public string? cwd { get; set; }

        [Description("是否后台执行，后台立即返回 task_id")]
        public bool? run_in_background { get; set; }

        [Description("后台任务说明，建议填写便于 task_list 识别")]
        public string? description { get; set; }

        [Description("超时秒数，前台默认 60，后台默认 600")]
        public int? timeout { get; set; }

        [Description("禁用超时，仅后台生效")]
        public bool? disable_timeout { get; set; }
    }

    /// <summary>工具参数：task_output</summary>
    private sealed class TaskOutputArgs
    {
        [Description("后台任务 id")]
        public string task_id { get; set; } = string.Empty;
    }

    /// <summary>工具参数：task_stop</summary>
    private sealed class TaskStopArgs
    {
        [Description("后台任务 id")]
        public string task_id { get; set; } = string.Empty;
    }

    /// <summary>工具参数：task_list（无参数）</summary>
    private sealed class TaskListArgs { }

    private async Task<string> RunAsync(BashArgs args)
    {
        var command = args.command?.Trim() ?? string.Empty;
        if (command.Length == 0)
        {
            throw new ArgumentException("command 参数不能为空");
        }
        if (args.run_in_background == true)
        {
            return StartBackground(command, args.cwd, args.timeout, args.disable_timeout == true, args.description);
        }

        int timeout = args.timeout ?? DefaultSyncTimeoutSeconds;
        if (timeout <= 0)
        {
            throw new ArgumentException("timeout 必须大于 0");
        }
        return await _sync.Value.RunCommandAsync(command, args.cwd, timeout);
    }

    /// <summary>启动后台任务：独立终端实例，立即返回 task_id</summary>
    private string StartBackground(string command, string? cwd, int? timeout, bool disableTimeout, string? description)
    {
        CleanupExpiredTasks();
        var id = Guid.NewGuid().ToString("N")[..8];
        Terminal? terminal = null;
        try
        {
            terminal = Terminal.Create(user);
            int? effectiveTimeout = disableTimeout ? null : (timeout ?? DefaultBackgroundTimeoutSeconds);
            if (effectiveTimeout is <= 0)
            {
                throw new ArgumentException("timeout 必须大于 0");
            }
            var task = terminal.RunCommandAsync(command, cwd, effectiveTimeout);
            var info = new BackgroundTask(id, description ?? string.Empty, terminal, task, DateTime.Now);
            _tasks[id] = info;
            // 完成后主动通知所属会话的 Agent（fire-and-forget）
            _ = NotifyOnCompletionAsync(info);
            return $"后台任务已启动，task_id: {id}"
                + (string.IsNullOrEmpty(description) ? string.Empty : $"，说明：{description}")
                + "\n可用 task_list 查看状态，task_output 查询结果。";
        }
        catch
        {
            terminal?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 后台任务完成（成功或失败）后，主动通知所属会话的 Agent，让它拿到结果继续处理。
    /// 结果摘要限制长度避免撑爆上下文，完整输出仍可通过 task_output 获取；
    /// 通知使用 stackable 类型，Agent 忙碌时同类型通知会合并，避免积压。
    /// </summary>
    private async Task NotifyOnCompletionAsync(BackgroundTask info)
    {
        string message;
        try
        {
            var result = await info.Task;
            message = $"后台任务 {Label(info)} 已完成：\n{CapResult(result)}";
        }
        catch (Exception ex)
        {
            message = $"后台任务 {Label(info)} 执行失败：{ex.Message}";
        }

        try
        {
            var session = await _sessionManager.GetSessionAsync(_sessionId);
            await session.Chat(message, type: "task_result", stackable: true);
        }
        catch (Exception)
        {
            // 通知失败（如会话已关闭）不影响后台任务本身，忽略即可
        }
    }

    private static string Label(BackgroundTask info) =>
        string.IsNullOrEmpty(info.Description) ? info.Id : $"{info.Id}（{info.Description}）";

    private static string CapResult(string text) =>
        text.Length <= NotifyResultLimit
            ? text
            : text[..NotifyResultLimit] + $"\n…（输出过长已截断，全文共 {text.Length} 字符，可用 task_output 获取完整结果）";

    private string ListTasks()
    {
        CleanupExpiredTasks();
        if (_tasks.IsEmpty)
        {
            return "当前没有后台任务。";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"后台任务（共 {_tasks.Count} 个）：");
        foreach (var kvp in _tasks)
        {
            var t = kvp.Value;
            string status = t.Task.IsCompleted ? "已完成" : "执行中";
            sb.AppendLine($"- {t.Id} [{status}] {t.Description}（已耗时 {(DateTime.Now - t.StartTime).TotalSeconds:F0}秒）");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> QueryTaskAsync(TaskOutputArgs args)
    {
        var id = args.task_id?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("task_id 参数不能为空");
        }
        if (!_tasks.TryGetValue(id, out var info))
        {
            return $"未找到任务 {id}，可能已过期或从未存在。";
        }
        if (!info.Task.IsCompleted)
        {
            var elapsed = (DateTime.Now - info.StartTime).TotalSeconds;
            return $"任务 {id} 仍在执行中（已等待 {elapsed:F0}秒），请稍后再查询。";
        }
        _tasks.TryRemove(id, out _);
        info.Terminal.Dispose();
        var result = await info.Task;
        return $"任务 {id} 已完成：\n{result}";
    }

    private Task<string> StopTaskAsync(TaskStopArgs args)
    {
        var id = args.task_id?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("task_id 参数不能为空");
        }
        if (!_tasks.TryRemove(id, out var info))
        {
            return Task.FromResult($"未找到任务 {id}，可能已过期或从未存在。");
        }
        info.Terminal.Dispose(); // Kill 终端进程，命令随之终止
        return Task.FromResult($"任务 {id} 已终止。");
    }

    /// <summary>清理超龄后台任务，防止任务表无限增长</summary>
    private void CleanupExpiredTasks()
    {
        foreach (var kvp in _tasks)
        {
            if (DateTime.Now - kvp.Value.StartTime > MaxTaskAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var info))
                {
                    info.Terminal.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_sync.IsValueCreated)
        {
            _sync.Value.Dispose();
        }
        foreach (var kvp in _tasks)
        {
            if (_tasks.TryRemove(kvp.Key, out var info))
            {
                info.Terminal.Dispose();
            }
        }
    }
}
