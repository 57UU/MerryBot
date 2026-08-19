using CommonLib;
using LlmBackend;
using LlmClient;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace Agent.Tools;

/// <summary>
/// 子任务工具集：注册 subagent / subagent_output / subagent_stop 三个工具。
/// 派发子任务时通过 Agent.Agent.Create 构造一个全新上下文（不持久化）的 Agent，
/// 但复用父会话同一个模型客户端（Client）、AgentOptions 与同一份工具列表
/// （不含本工具集自身，因此不允许嵌套派生子任务），在后台执行。
/// 子任务完成或失败时通过 notifyAsync 回调注入所属主会话
/// （type: "subagent_result"，stackable 合并同类），主 Agent 可继续处理。
/// </summary>
public class SubAgentToolSet : ToolSet, IDisposable
{
    /// <summary>已完成子任务结果的保留时长；只清理"已完成且超龄"的结果，运行中的任务按自身取消管理</summary>
    private static readonly TimeSpan MaxTaskAge = TimeSpan.FromMinutes(5);
    /// <summary>完成通知的结果摘要长度限制（字符），全文可通过 subagent_output 获取</summary>
    private const int NotifyResultLimit = 2000;

    private readonly ToolSetBridge bridge;
    private readonly LlmClient.Client _llmClient;
    private readonly int _tokenLimit;
    private readonly AgentOptions _options;
    private readonly IList<ToolSet> _tools;
    private readonly Func<string, Task> _notifyAsync;
    private readonly CancellationToken _shutdownToken;
    /// <summary>同时运行中的子任务数上限（每个子任务=一次完整 LLM 调用），防成本失控</summary>
    private readonly int _maxSubagents;
    private readonly ConcurrentDictionary<string, SubTask> _tasks = new();

    private sealed record SubTask(string Id, string TaskText, Task<string> Result, CancellationTokenSource Cts, DateTime StartTime)
    {
        /// <summary>任务被显式终止（subagent_stop 等）后置位，抑制"已完成"通知</summary>
        public volatile bool Stopped;
    }

    /// <summary>
    /// 创建子任务工具集。llmClient / options / tools 均为父会话同一实例（模型与工具复用）；
    /// notifyAsync 由宿主注入：通常为"向所属主会话 Chat 注入消息（type: subagent_result, stackable: true）"；
    /// shutdownToken 在宿主（插件）生命周期结束时取消全部运行中的子任务。
    /// </summary>
    public SubAgentToolSet(
        LlmClient.Client llmClient,
        int tokenLimit,
        AgentOptions options,
        IList<ToolSet> tools,
        Func<string, Task> notifyAsync,
        CancellationToken shutdownToken,
        int maxSubagents = 3)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _tokenLimit = tokenLimit;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _notifyAsync = notifyAsync ?? throw new ArgumentNullException(nameof(notifyAsync));
        _shutdownToken = shutdownToken;
        _maxSubagents = Math.Max(1, maxSubagents);

        var builder = new ToolSetBridge.Builder(
            "如需把任务交给独立的子 Agent 处理，调用 subagent 工具：子 Agent 拥有全新上下文，" +
            "与主 Agent 共享模型与全部工具，任务在后台执行，完成后会自动收到结果通知；用 task_id 管理。");
        builder.AddFunction<SubagentArgs>("subagent",
            "派发一个异步子任务：创建全新上下文的子 Agent 执行给定任务，立即返回 task_id，不阻塞当前处理；" +
            "子任务完成后结果会自动注入对话，也可用 subagent_output 查询全文、subagent_stop 终止。",
            StartSubagentAsync);
        builder.AddFunction<SubagentOutputArgs>("subagent_output",
            "查询子任务结果：未完成返回执行中提示，已完成返回结果全文并移除任务。",
            QuerySubagentAsync);
        builder.AddFunction<SubagentStopArgs>("subagent_stop",
            "终止指定子任务。",
            StopSubagentAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
        => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：subagent</summary>
    private sealed class SubagentArgs
    {
        [Description("要交给子 Agent 完成的任务描述")]
        public string task { get; set; } = string.Empty;

        [Description("子 Agent 的系统提示（必填），子 Agent 与主 Agent 共享模型与工具，但系统提示由本次调用指定")]
        public string system_prompt { get; set; } = string.Empty;
    }

    /// <summary>工具参数：subagent_output</summary>
    private sealed class SubagentOutputArgs
    {
        [Description("子任务 id")]
        public string task_id { get; set; } = string.Empty;
    }

    /// <summary>工具参数：subagent_stop</summary>
    private sealed class SubagentStopArgs
    {
        [Description("子任务 id")]
        public string task_id { get; set; } = string.Empty;
    }

    /// <summary>启动子任务：立即返回 task_id，后台执行；完成后由 NotifyOnCompletionAsync 通知主会话</summary>
    private Task<string> StartSubagentAsync(SubagentArgs args)
    {
        var task = args.task?.Trim() ?? string.Empty;
        if (task.Length == 0)
        {
            throw new ArgumentException("task 参数不能为空");
        }
        if (string.IsNullOrWhiteSpace(args.system_prompt))
        {
            throw new ArgumentException("system_prompt 参数不能为空");
        }
        CleanupExpiredTasks();
        // 运行中子任务数上限：防止 LLM 派发大量子任务（每个=一次完整 LLM 调用）导致成本/资源失控
        var runningCount = _tasks.Values.Count(t => !t.Result.IsCompleted);
        if (runningCount >= _maxSubagents)
        {
            return Task.FromResult($"子任务已达上限（{_maxSubagents} 个运行中），请先等待现有子任务完成（subagent_output 查询）或终止（subagent_stop）后再派发新任务。");
        }
        var id = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        // 子 Agent 的 SystemPrompt 由调用方指定（必填），其余选项复用父会话
        var options = new AgentOptions
        {
            SystemPrompt = args.system_prompt,
            MaxIterations = _options.MaxIterations,
            MaxConcurrentToolCalls = _options.MaxConcurrentToolCalls,
            ContextCompactRatio = _options.ContextCompactRatio,
            MaxOutputTokens = _options.MaxOutputTokens,
            ReasoningEffort = _options.ReasoningEffort,
            OnLog = _options.OnLog,
        };
        var resultTask = RunSubagentAsync(task, options, cts.Token);
        var info = new SubTask(id, task, resultTask, cts, DateTime.Now);
        _tasks[id] = info;
        // 完成后主动通知所属会话的 Agent（fire-and-forget）
        _ = NotifyOnCompletionAsync(info);
        return Task.FromResult($"子任务已启动，task_id: {id}"
            + $"\n任务：{(task.Length > 60 ? task[..60] + "…" : task)}"
            + "\n完成后会自动收到结果通知，也可用 subagent_output 查询全文、subagent_stop 终止。");
    }

    /// <summary>
    /// 构造全新上下文（contextHistory 传 null，不持久化）的子 Agent 并执行任务，
    /// task 即首个用户消息；复用父会话的模型客户端、options 与工具列表。
    /// </summary>
    private async Task<string> RunSubagentAsync(string taskText, AgentOptions options, CancellationToken cancellationToken)
    {
        // 命名空间 Agent.Tools 内 "Agent" 会被类 Agent 遮蔽，需 global:: 前缀（与 Agent.Tui 一致）
        var agent = await global::Agent.Agent.Create(null, _llmClient, _tokenLimit, options, _tools);
        var (result, _) = await agent.Chat(taskText, cancellationToken);
        return result;
    }

    /// <summary>
    /// 子任务完成（成功或失败）后，主动通知所属会话的 Agent，让它拿到结果继续处理。
    /// 结果摘要限制长度避免撑爆上下文，完整输出仍可通过 subagent_output 获取；
    /// 通知使用 stackable 类型，Agent 忙碌时同类型通知会合并，避免积压。
    /// 被 subagent_stop 等显式终止或取消的任务不推送通知，避免误导。
    /// </summary>
    private async Task NotifyOnCompletionAsync(SubTask info)
    {
        string message;
        try
        {
            var result = await info.Result;
            if (info.Stopped)
            {
                return;
            }
            message = $"子任务 {Label(info)} 已完成：\n{CapResult(result)}";
        }
        catch (Exception ex)
        {
            if (info.Stopped || info.Cts.IsCancellationRequested)
            {
                return; // 显式终止或取消：不推送"已完成/失败"误导通知
            }
            SimpleLog.Default.Warn($"子任务 {Label(info)} 执行失败: {ex.Message}");
            message = $"子任务 {Label(info)} 执行失败：{ex.Message}";
        }

        try
        {
            await _notifyAsync(message);
        }
        catch (Exception ex)
        {
            // 通知失败（如会话已关闭）不影响子任务本身，忽略即可
            SimpleLog.Default.Warn($"子任务 {Label(info)} 完成通知投递失败: {ex.Message}");
        }
    }

    private static string Label(SubTask info) =>
        info.TaskText.Length <= 30 ? $"{info.Id}（{info.TaskText}）" : $"{info.Id}（{info.TaskText[..30]}…）";

    private static string CapResult(string text) =>
        text.Length <= NotifyResultLimit
            ? text
            : text[..NotifyResultLimit] + $"\n…（输出过长已截断，全文共 {text.Length} 字符，可用 subagent_output 获取完整结果）";

    private async Task<string> QuerySubagentAsync(SubagentOutputArgs args)
    {
        var id = args.task_id?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("task_id 参数不能为空");
        }
        if (!_tasks.TryGetValue(id, out var info))
        {
            return $"未找到子任务 {id}，可能已过期或从未存在。";
        }
        if (!info.Result.IsCompleted)
        {
            var elapsed = (DateTime.Now - info.StartTime).TotalSeconds;
            return $"子任务 {id} 仍在执行中（已等待 {elapsed:F0}秒），请稍后再查询。";
        }
        _tasks.TryRemove(id, out _);
        info.Cts.Dispose();
        try
        {
            var result = await info.Result;
            return $"子任务 {id} 已完成：\n{result}";
        }
        catch (Exception ex)
        {
            return $"子任务 {id} 执行失败：{ex.Message}";
        }
    }

    private Task<string> StopSubagentAsync(SubagentStopArgs args)
    {
        var id = args.task_id?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("task_id 参数不能为空");
        }
        if (!_tasks.TryRemove(id, out var info))
        {
            return Task.FromResult($"未找到子任务 {id}，可能已过期或从未存在。");
        }
        info.Stopped = true; // 先置位抑制完成通知，再取消生成
        info.Cts.Cancel();
        info.Cts.Dispose();
        return Task.FromResult($"子任务 {id} 已终止。");
    }

    /// <summary>清理"已完成且超龄"的子任务结果，防止任务表无限增长；运行中的任务不被强杀</summary>
    private void CleanupExpiredTasks()
    {
        foreach (var kvp in _tasks)
        {
            if (kvp.Value.Result.IsCompleted && DateTime.Now - kvp.Value.StartTime > MaxTaskAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var info))
                {
                    info.Stopped = true;
                    info.Cts.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _tasks)
        {
            if (_tasks.TryRemove(kvp.Key, out var info))
            {
                info.Stopped = true;
                info.Cts.Cancel();
                info.Cts.Dispose();
            }
        }
    }
}
