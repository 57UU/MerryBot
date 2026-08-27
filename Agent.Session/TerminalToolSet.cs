using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CommonLib;
using LlmBackend;

namespace Agent.Session;

/// <summary>
/// 终端工具集：注册 bash / task_list / task_output / task_stop 四个工具，效果对齐内置 Bash 工具。
/// bash 进程懒加载——构造时不启动，首次前台调用时才创建共享常驻终端，之后跨调用保留 shell 状态（如 cd）。
/// user 构造参数非空时以 sudo -u user 运行 bash；后台任务各自使用独立终端实例。
/// 前台命令可通过 background_on_timeout 在超时时自动转为后台任务（复用同一套后台设施），
/// 完成时通过 sessionManager 主动通知所属 session 的 Agent（stackable 类型，避免积压），
/// 消息正文使用 &lt;TERMINAL_TASK_RESULT&gt; XML 标签包裹。
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
    /// <summary>shell 进程的初始工作目录；为 null 时由 Terminal 继承进程 CWD</summary>
    private readonly string? _initialWorkingDirectory;
    /// <summary>同时运行中的后台任务数上限（每个后台任务=独立 Terminal 进程），防进程风暴/资源耗尽</summary>
    private readonly int _maxBackgroundTasks;
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
            candidates = ["bash", "pwsh", "powershell", "cmd"];
        }
        else
        {
            candidates = ["/bin/bash", "/bin/sh"];
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
            ? [string.Empty, ".exe", ".com", ".bat", ".cmd", ".ps1"]
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

    private sealed record BackgroundTask(string Id, string Description, IDisposable Owner, Task<string> Task, DateTime StartTime)
    {
        /// <summary>任务被显式终止（task_stop 等）后置位，抑制"已完成"通知</summary>
        public volatile bool Stopped;

        /// <summary>是否由前台超时自动转入后台（通知中标注来源）；Owner 为转后台句柄而非独立终端</summary>
        public bool FromForegroundTimeout;
    }

    public TerminalToolSet(
        AgentSessionManager sessionManager,
        string sessionId,
        string? user = null,
        VisionRouter? visionRouter = null,
        int maxImageBytes = 10 * 1024 * 1024,
        string? initialWorkingDirectory = null,
        int maxBackgroundTasks = 5)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sessionId = sessionId;
        this.user = user;
        _visionRouter = visionRouter ?? new VisionRouter(mainHasVision: false, visionClients: null);
        _maxImageBytes = maxImageBytes;
        _initialWorkingDirectory = initialWorkingDirectory;
        _maxBackgroundTasks = Math.Max(1, maxBackgroundTasks);
        if (!string.IsNullOrEmpty(user) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("user（sudo 模式）仅支持 Linux");
        }
        // 先探测 shell，前后台统一使用，检测结果同时写入工具 prompt
        _detectedShell = DetectShell();
        // 懒加载：首次前台调用取 .Value 时才启动共享常驻终端进程
        _sync = new Lazy<Terminal>(() => Terminal.Create(_detectedShell, user, _initialWorkingDirectory));

        // 图片查看能力：主模型或辅助视觉模型任一可用才注册 load_local_image 工具
        var hasVision = _visionRouter.MainHasVision || _visionRouter.HasVisionFallback;
        var prompt =
            $"如需执行命令，调用 shell 工具；当前检测到的 shell 为：{_detectedShell}，请使用该 shell 的正确语法编写命令。命令在常驻 shell 中执行，cd 等状态跨调用保留；长任务可后台执行，用 task_list / task_output / task_stop 管理。";
        var builder = new ToolSetBridge.Builder(prompt);
        var shellDescription = "执行 shell 命令并返回输出。前台（默认）：在共享常驻 shell 中串行执行，同步返回输出，默认超时 60 秒，超时后终止并重启 shell；" +
              "设置 background_on_timeout=true 时超时不终止命令，而是自动转入后台继续运行并返回 task_id；" +
              "run_in_background=true 时后台执行，立即返回 task_id，之后用 task_output 查询结果，默认超时 600 秒，disable_timeout=true 则不设超时。";
        builder.AddFunction<BashArgs>("shell", shellDescription,
            (BashArgs args, CancellationToken ct, Action<Message> onIterationAdd) => RunAsync(args, ct));
        if (hasVision)
        {
            builder.AddFunction<LoadLocalImageArgs>("load_local_image",
                "加载本地图片文件并注入对话供模型查看。相对路径按 shell 当前工作目录解析（cd 状态跨调用保留）。",
                LoadLocalImageAsync);
        }
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

    /// <summary>复制终端配置但不共享常驻 shell 或后台任务。</summary>
    public override ToolSet Copy() => new TerminalToolSet(
        _sessionManager,
        _sessionId,
        user,
        _visionRouter,
        _maxImageBytes,
        _initialWorkingDirectory,
        _maxBackgroundTasks);

    /// <summary>工具参数：bash</summary>
    private class BashArgs
    {
        [Description("要执行的命令")]
        public string command { get; set; } = string.Empty;

        [Description("工作目录，如 /tmp")]
        public string? cwd { get; set; }

        [Description("是否后台执行，后台立即返回 task_id")]
        public bool? run_in_background { get; set; }

        [Description("前台超时时不终止命令，而是自动转为后台任务继续运行并返回 task_id，默认 false")]
        public bool? background_on_timeout { get; set; }

        [Description("后台任务说明，建议填写便于 task_list 识别")]
        public string? description { get; set; }

        [Description("超时秒数，前台默认 60，后台默认 600")]
        public int? timeout { get; set; }

        [Description("禁用超时，仅后台生效")]
        public bool? disable_timeout { get; set; }
    }

    /// <summary>工具参数：load_local_image</summary>
    private sealed class LoadLocalImageArgs
    {
        [Description("要加载的图片文件路径，相对路径按 shell 当前工作目录解析")]
        public string image_path { get; set; } = string.Empty;

        [Description("工作目录，如 /tmp；相对路径基于此解析，未提供则按常驻 shell 的 pwd 解析")]
        public string? cwd { get; set; }
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

    private async Task<string> RunAsync(BashArgs args, CancellationToken cancellationToken)
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
        var result = await _sync.Value.RunCommandAsync(command, args.cwd, timeout, args.background_on_timeout == true);
        if (result.Detached == null)
        {
            return result.Output;
        }
        return RegisterConvertedTask(result.Detached, args.description,
            $"命令前台执行超时（{timeout} 秒），已自动转入后台继续运行");
    }

    /// <summary>
    /// 登记一个由前台超时自动转入后台的任务：复用现有后台任务设施
    /// （task_list/task_output/task_stop/完成通知/过期清理），转换后的任务持有转后台句柄而非独立终端。
    /// 不做运行数上限检查——命令已经在跑，拒绝登记只会让它脱离管理。
    /// </summary>
    private string RegisterConvertedTask(TerminalDetachedCommand detached, string? description, string headline)
    {
        CleanupExpiredTasks();
        var id = Guid.NewGuid().ToString("N")[..8];
        var info = new BackgroundTask(id, description ?? string.Empty, detached, detached.Completion, DateTime.Now)
        {
            FromForegroundTimeout = true,
        };
        _tasks[id] = info;
        _ = NotifyOnCompletionAsync(info);
        return headline + $"，task_id: {id}"
            + (string.IsNullOrEmpty(description) ? string.Empty : $"，说明：{description}")
            + "\n可用 task_list 查看状态，task_output 查询结果。";
    }

    /// <summary>
    /// 加载本地图片并注入对话：主模型有视觉能力时通过调用级回调把图片注入对话，
    /// 否则调用辅助视觉模型生成描述返回。相对路径按常驻 shell 的当前工作目录解析
    /// （cd 状态跨调用保留，进程 CWD 并不等于 shell CWD）。
    /// </summary>
    private async Task<string> LoadLocalImageAsync(LoadLocalImageArgs args, CancellationToken cancellationToken, Action<Message> onIterationAdd)
    {
        var imagePath = args.image_path?.Trim() ?? string.Empty;
        if (imagePath.Length == 0)
        {
            throw new ArgumentException("image_path 参数不能为空");
        }

        string fullPath;
        try
        {
            fullPath = await ResolveShellPath(imagePath, args.cwd);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不是路径解析失败：继续传播，由 Agent 统一回填取消结果
        }
        catch (Exception e)
        {
            return $"[image_path 无效: {imagePath}: {e.Message}]";
        }
        if (!File.Exists(fullPath))
        {
            return $"[图片文件不存在: {imagePath}（按 shell 工作目录解析为 {fullPath}）]";
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _maxImageBytes)
        {
            return $"[图片 {imagePath} 超过 {_maxImageBytes / (1024 * 1024)}MB，已拒绝读取]";
        }
        var data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var mimeType = MimeTypes.GuessImageContentType(fullPath) ?? "image/png";
        var caption = $"本地图片: {imagePath}";

        if (_visionRouter.MainHasVision)
        {
            onIterationAdd(VisionRouter.BuildImageMessage(data, mimeType, caption));
            return $"[图片已注入对话: {imagePath}]";
        }
        if (!_visionRouter.HasVisionFallback)
        {
            return $"[无法查看图片 {imagePath}: 主模型无视觉能力且未配置 vision-llm]";
        }

        var description = await _visionRouter.DescribeImageAsync(data, mimeType, imagePath, cancellationToken);
        return $"[图片 {imagePath} 描述]: {description}";
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
            var shellPwd = (await _sync.Value.RunCommandAsync("pwd", null, 5)).Output.Trim();
            if (!string.IsNullOrEmpty(shellPwd))
            {
                return Path.GetFullPath(Path.Combine(shellPwd, imagePath));
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不参与路径降级：继续传播
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
        // 运行中后台任务数上限：每个后台任务=独立 Terminal 进程，防 LLM 派发进程风暴耗尽系统资源
        var runningCount = _tasks.Values.Count(t => !t.Task.IsCompleted);
        if (runningCount >= _maxBackgroundTasks)
        {
            return $"后台任务已达上限（{_maxBackgroundTasks} 个运行中），请先等待现有任务完成（task_output 查询）或终止（task_stop）后再启动新任务。";
        }
        var id = Guid.NewGuid().ToString("N")[..8];
        Terminal? terminal = null;
        try
        {
            terminal = Terminal.Create(_detectedShell, user, _initialWorkingDirectory);
            int? effectiveTimeout = disableTimeout ? null : (timeout ?? DefaultBackgroundTimeoutSeconds);
            if (effectiveTimeout is <= 0)
            {
                throw new ArgumentException("timeout 必须大于 0");
            }
            var task = RunAndUnwrapAsync(terminal, command, cwd, effectiveTimeout);
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

    /// <summary>后台任务专用：执行命令并只取输出文本（RunCommandAsync 现返回 TerminalRunResult，后台路径不涉及转后台句柄）</summary>
    private static async Task<string> RunAndUnwrapAsync(Terminal terminal, string command, string? cwd, int? timeoutSeconds)
        => (await terminal.RunCommandAsync(command, cwd, timeoutSeconds)).Output;

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
            var fields = new List<(string Name, string? Value)>
            {
                ("task_id", info.Id),
                ("status", "completed"),
                ("description", info.Description),
            };
            if (info.FromForegroundTimeout)
            {
                fields.Add(("note", "前台超时自动转入后台"));
            }
            fields.Add(("output", CapResult(result)));
            message = global::Agent.AgentEventMessageFormatter.Format("TERMINAL_TASK_RESULT", fields.ToArray());
        }
        catch (Exception ex)
        {
            if (info.Stopped)
            {
                return;
            }
            message = global::Agent.AgentEventMessageFormatter.Format(
                "TERMINAL_TASK_RESULT",
                ("task_id", info.Id),
                ("status", "failed"),
                ("description", info.Description),
                ("error", ex.Message));
        }

        try
        {
            var session = await _sessionManager.GetSessionAsync(_sessionId);
            session.EnqueueStackable("tool_result", info.Id, message,
                () => new StackableMessage(null, CancellationToken.None, null));
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
        info.Owner.Dispose();
        var result = await info.Task;
        // 已通过拉取拿到全文：撤回排队中的完成通知，避免同一结果经"推送"与"拉取"双通道重复投递
        try
        {
            var session = await _sessionManager.GetSessionAsync(_sessionId);
            session.RemoveQueued("tool_result", id);
        }
        catch (Exception ex)
        {
            // 撤回失败不影响拉取结果本身
            SimpleLog.Default.Warn($"任务 {id} 撤回排队通知失败: {ex.Message}");
        }
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
        info.Owner.Dispose(); // Kill 终端进程（含子进程树），命令随之终止
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
                    info.Owner.Dispose();
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
                info.Owner.Dispose();
            }
        }
    }
}
