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
    /// <summary>已完成后台任务结果的保留时长；只清理"已完成且超龄"的结果，运行中的任务按自身超时管理</summary>
    private static readonly TimeSpan MaxTaskAge = TimeSpan.FromMinutes(5);
    /// <summary>后台任务完成通知的结果摘要长度限制（字符）</summary>
    private const int NotifyResultLimit = 2000;

    private readonly ToolSetBridge bridge;
    private readonly AgentSessionManager _sessionManager;
    private readonly string _sessionId;
    private readonly string? user;
    private readonly VisionRouter _visionRouter;
    /// <summary>图片读取大小上限（字节），防止超大图片撑爆内存</summary>
    private readonly int _maxImageBytes;
    /// <summary>构造时按平台探测到的 shell，前后台终端创建与 prompt 注入共用</summary>
    private readonly string _detectedShell;
    private readonly Lazy<Terminal> _sync;
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();

    /// <summary>
    /// 按平台优先级探测可用 shell：
    ///   Windows: bash → pwsh → powershell → cmd
    ///   Linux/Unix: /bin/bash → /bin/sh
    /// 先查绝对路径（File.Exists），再扫 PATH 环境变量中的目录，返回第一个命中的可执行文件。
    /// </summary>
    private static string DetectShell()
    {
        IEnumerable<string> candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates = new[] { "bash", "pwsh", "powershell", "cmd" };
        }
        else
        {
            candidates = new[] { "/bin/bash", "/bin/sh" };
        }

        // 候选可能是绝对路径，先直接试 File.Exists
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            if (IsOnPath(candidate)) return candidate;
        }

        throw new PlatformNotSupportedException("未检测到任何可用 shell");
    }

    /// <summary>在 PATH 环境变量中查找指定可执行文件（Windows 自动追加 .exe/.com/.bat 等扩展）</summary>
    private static bool IsOnPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;
        var paths = pathEnv.Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':');
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { string.Empty, ".exe", ".com", ".bat", ".cmd", ".ps1" }
            : new[] { string.Empty };
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                foreach (var ext in extensions)
                {
                    var full = Path.Combine(dir, executable + ext);
                    if (File.Exists(full)) return true;
                }
            }
            catch
            {
                // 单个目录无权访问等异常跳过，继续下一个
            }
        }
        return false;
    }

    private sealed record BackgroundTask(string Id, string Description, Terminal Terminal, Task<string> Task, DateTime StartTime)
    {
        /// <summary>任务被显式终止（task_stop 等）后置位，抑制"已完成"通知</summary>
        public volatile bool Stopped;
    }

    public TerminalToolSet(
        AgentSessionManager sessionManager,
        string sessionId,
        string? user = null,
        VisionRouter? visionRouter = null,
        int maxImageBytes = 10 * 1024 * 1024)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sessionId = sessionId;
        this.user = user;
        _visionRouter = visionRouter ?? new VisionRouter(mainHasVision: false, visionClients: null);
        _maxImageBytes = maxImageBytes;
        if (!string.IsNullOrEmpty(user) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("user（sudo 模式）仅支持 Linux");
        }
        // 先探测 shell，前后台统一使用，检测结果同时写入工具 prompt
        _detectedShell = DetectShell();
        // 懒加载：首次前台调用取 .Value 时才启动共享常驻终端进程
        _sync = new Lazy<Terminal>(() => Terminal.Create(_detectedShell, user));

        var prompt =
            $"如需执行命令，调用 shell 工具；当前检测到的 shell 为：{_detectedShell}，请使用该 shell 的正确语法编写命令。命令在常驻 shell 中执行，cd 等状态跨调用保留；长任务可后台执行，用 task_list / task_output / task_stop 管理。";
        var builder = new ToolSetBridge.Builder(prompt);
        builder.AddFunction<BashArgs>("shell",
            "执行 shell 命令并返回输出。前台（默认）：在共享常驻 shell 中串行执行，同步返回输出，默认超时 60 秒，超时后终止并重启 shell；" +
            "run_in_background=true 时后台执行，立即返回 task_id，之后用 task_output 查询结果，默认超时 600 秒，disable_timeout=true 则不设超时；" +
            "image_path 用于命令生成图片后附带查看（如 python 绘图输出的 png），主模型有视觉能力时直接查看，否则由辅助视觉模型描述。",
            RunAsync);
        builder.AddFunction<TaskListArgs>("task_list",
            "列出所有后台 shell 任务：id、说明、运行中/已完成、已耗时。",
            _ => Task.FromResult(ListTasks()));
        builder.AddFunction<TaskOutputArgs>("task_output",
            "查询后台 shell 任务结果：未完成返回执行中提示，已完成返回结果并移除任务。",
            QueryTaskAsync);
        builder.AddFunction<TaskStopArgs>("task_stop",
            "终止指定后台 shell 任务。",
            StopTaskAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
        => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
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

        [Description("命令执行后要查看的图片文件路径（可选）。命令生成了图片（如图表、截图）时提供，模型会直接查看或用辅助视觉模型描述；相对路径按 shell 当前工作目录解析")]
        public string? image_path { get; set; }
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

    private async Task<string> RunAsync(BashArgs args, CancellationToken cancellationToken, Action<Message> onIterationAdd)
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
        var output = await _sync.Value.RunCommandAsync(command, args.cwd, timeout);
        return await AppendImageIfRequestedAsync(output, args.image_path, args.cwd, cancellationToken, onIterationAdd);
    }

    /// <summary>
    /// 命令输出后按 image_path 附带查看图片：主模型有视觉能力时通过调用级回调
    /// 把图片注入对话，否则调用辅助视觉模型生成描述并追加到输出。
    /// 相对路径按常驻 shell 的当前工作目录解析（cd 状态跨调用保留，进程 CWD 并不等于 shell CWD）。
    /// </summary>
    private async Task<string> AppendImageIfRequestedAsync(string output, string? imagePath, string? cwd, CancellationToken cancellationToken, Action<Message> onIterationAdd)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return output;
        }

        string fullPath;
        try
        {
            fullPath = await ResolveShellPath(imagePath, cwd);
        }
        catch (Exception e)
        {
            return output + $"\n[image_path 无效: {imagePath}: {e.Message}]";
        }
        if (!File.Exists(fullPath))
        {
            return output + $"\n[图片文件不存在: {imagePath}（按 shell 工作目录解析为 {fullPath}）]";
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _maxImageBytes)
        {
            return output + $"\n[图片 {imagePath} 超过 {_maxImageBytes / (1024 * 1024)}MB，已拒绝读取]";
        }
        var data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var mimeType = MimeTypes.GuessImageContentType(fullPath) ?? "image/png";
        var caption = $"bash 命令输出中的图片: {imagePath}";

        if (_visionRouter.MainHasVision)
        {
            onIterationAdd(VisionRouter.BuildImageMessage(data, mimeType, caption));
            return output + $"\n[图片已注入对话: {imagePath}]";
        }
        if (!_visionRouter.HasVisionFallback)
        {
            return output + $"\n[无法查看图片 {imagePath}: 主模型无视觉能力且未配置 vision-llm]";
        }

        var description = await _visionRouter.DescribeImageAsync(data, mimeType, imagePath, cancellationToken);
        return output + $"\n[图片 {imagePath} 描述]: {description}";
    }

    /// <summary>
    /// 解析图片路径：绝对路径直接用；相对路径先查常驻 shell 的 pwd（反映 cd 后的真实目录），
    /// 查不到再退回 cwd 参数，最后退回进程工作目录。
    /// </summary>
    private async Task<string> ResolveShellPath(string imagePath, string? cwd)
    {
        if (Path.IsPathRooted(imagePath))
        {
            return Path.GetFullPath(imagePath);
        }
        try
        {
            var shellPwd = (await _sync.Value.RunCommandAsync("pwd", null, 5)).Trim();
            if (!string.IsNullOrEmpty(shellPwd))
            {
                return Path.GetFullPath(Path.Combine(shellPwd, imagePath));
            }
        }
        catch
        {
            // shell 查询失败时退回 cwd 参数解析
        }
        var baseDir = string.IsNullOrWhiteSpace(cwd) ? "." : cwd;
        return Path.GetFullPath(Path.Combine(baseDir, imagePath));
    }

    /// <summary>启动后台任务：独立终端实例，立即返回 task_id</summary>
    private string StartBackground(string command, string? cwd, int? timeout, bool disableTimeout, string? description)
    {
        CleanupExpiredTasks();
        var id = Guid.NewGuid().ToString("N")[..8];
        Terminal? terminal = null;
        try
        {
            terminal = Terminal.Create(_detectedShell, user);
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
    /// 被 task_stop 等显式终止的任务不推送通知，避免误导。
    /// </summary>
    private async Task NotifyOnCompletionAsync(BackgroundTask info)
    {
        string message;
        try
        {
            var result = await info.Task;
            if (info.Stopped)
            {
                return; // 任务已被 task_stop 等显式终止，不推送"已完成"误导通知
            }
            message = $"后台任务 {Label(info)} 已完成：\n{CapResult(result)}";
        }
        catch (Exception ex)
        {
            if (info.Stopped)
            {
                return;
            }
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
        info.Stopped = true; // 抑制完成通知，避免推送误导性的"已完成"消息
        info.Terminal.Dispose(); // Kill 终端进程（含子进程树），命令随之终止
        return Task.FromResult($"任务 {id} 已终止。");
    }

    /// <summary>清理"已完成且超龄"的后台任务结果，防止任务表无限增长；运行中的任务按自身超时管理，不被强杀</summary>
    private void CleanupExpiredTasks()
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Task.IsCompleted && DateTime.Now - kvp.Value.StartTime > MaxTaskAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var info))
                {
                    info.Stopped = true;
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
                info.Stopped = true;
                info.Terminal.Dispose();
            }
        }
    }
}
