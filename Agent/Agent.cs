using LlmBackend;
using LlmClient;
using System.Text;

namespace Agent;

public partial class Agent
{
    private ContextManager contextManager;
    private Client llmClient;
    private AgentOptions options;
    private IList<ToolSet>? toolSets;
    public string SystemPrompt { get; private set; }
    private Agent(
        ContextManager contextManager,
        Client llmClient,
        AgentOptions options,
        IList<ToolSet> toolSets
        )
    {
        this.contextManager = contextManager;
        this.llmClient = llmClient;
        this.options = options;
        this.toolSets = toolSets;
        //
        StringBuilder sb = new(options.SystemPrompt);
        foreach (var toolSet in toolSets)
        {
            var prompt = toolSet.Prompt();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                sb.AppendLine(prompt);
            }
        }
        SystemPrompt = sb.ToString();
    }
    public static async Task<Agent> Create(
        ContextHistory? contextHistory,
        Client llmClient,
        int tokenLimit,
        AgentOptions? options,
        IList<ToolSet>? toolSets
        )
    {
        var contextManager = await ContextManager.Create(contextHistory, tokenLimit);
        var agent = new Agent(contextManager, llmClient, options ?? new AgentOptions(), toolSets ?? []);
        return agent;
    }
    private async Task Compact(CancellationToken cancellationToken, int iteration, string? topic)
    {
        Log(new AgentLogEvent(
            AgentLogEventKind.ContextCompaction,
            DateTimeOffset.UtcNow,
            iteration,
            Result: "started"));
        try
        {
            await contextManager.Compact(
                cancellationToken,
                (token, context) => CompactContext(token, context, iteration, topic));
            Log(new AgentLogEvent(
                AgentLogEventKind.ContextCompaction,
                DateTimeOffset.UtcNow,
                iteration,
                Result: "completed"));
        }
        catch (Exception exception)
        {
            Log(new AgentLogEvent(
                AgentLogEventKind.ContextCompaction,
                DateTimeOffset.UtcNow,
                iteration,
                Exception: exception));
            throw;
        }

    }
    private async Task<(string result, TokenUsage tokenUsage)> CompactContext(
        CancellationToken cancellationToken,
        Context context,
        int iteration,
        string? topic)
    {
        var forkedContext = context.Fork();
        // 指定 topic 时要求模型围绕该主题压缩
        var instruction = string.IsNullOrWhiteSpace(topic)
            ? "请将上文对话信息压缩为一个段落，无需保留system prompt"
            : $"请将上文对话信息压缩为一个段落，无需保留system prompt，重点保留与主题「{topic}」相关的内容";
        forkedContext.Messages.Add(Message.User(instruction));
        Log(new AgentLogEvent(AgentLogEventKind.ModelRequest, DateTimeOffset.UtcNow, iteration));
        // 压缩为纯文本摘要任务，从基准选项派生一个禁用工具的副本（WithoutTools 保留其余配置）：
        // 开启工具时模型可能返回 tool_calls 而摘要为空，导致 ContextManager 判定压缩失败、
        // 保留原上下文；同时避免压缩过程产生额外的工具执行消耗。
        var (result, tokenUsage) = await llmClient.Generate(cancellationToken, forkedContext.Messages, SystemPrompt, new LlmOptions().WithoutTools());
        Log(new AgentLogEvent(
            AgentLogEventKind.ModelResponse,
            DateTimeOffset.UtcNow,
            iteration,
            Result: result.Content,
            Usage: tokenUsage));
        return (result.Content!, tokenUsage);
    }

    public async Task<(string result, TokenUsage tokenUsage)> Chat(
        string userInput,
        CancellationToken cancellationToken)
    {
        Log(new AgentLogEvent(AgentLogEventKind.ChatStarted, DateTimeOffset.UtcNow));
        try
        {
            var messages = contextManager.context.Messages;
            if (!string.IsNullOrWhiteSpace(userInput))
            {
                var userMessage = Message.User(userInput);
                messages.Add(userMessage);
                RecordMessage(userMessage, TokenUsage.Zero);
            }

            var toolDefs = toolSets!.SelectMany(toolSet => toolSet.Tools()).ToList();
            var llmOptions = new LlmOptions
            {
                Tools = toolDefs.Count > 0 ? toolDefs : null,
                MaxTokens = options.MaxOutputTokens,
                ReasoningEffort = options.ReasoningEffort,
            };

            TokenUsage totalUsage = TokenUsage.Zero;
            // 最后一次调用 LLM 的用量：其 prompt 数即当前上下文真实大小（见下方压缩判断注释）
            TokenUsage lastUsage = TokenUsage.Zero;
            string? result = null;

            // 对话循环：直到模型不再请求工具调用或达到最大迭代次数。
            // 循环内不做上下文压缩：工具调用轮次刚把 tool 结果回填进消息列表，模型还需
            // 基于精确消息继续推理，此时压缩会把工具链替换成摘要，打断后续工具调用。
            for (int iteration = 0; iteration < options.MaxIterations; iteration++)
            {
                // 最后一次迭代不提供工具，强迫模型直接返回文本输出，避免收尾失败；
                // 必须保留 ReasoningEffort：anthropic 开启 thinking 后历史含思考块，
                // 请求突然关闭 thinking 会被 API 拒绝（thinking 块必须持续回传）
                var iterationOptions = iteration == options.MaxIterations - 1
                    ? llmOptions.WithoutTools()
                    : llmOptions;

                TokenUsage usage;
                string? iterationResult;
                (usage, iterationResult) = await RunIteration(
                    cancellationToken,
                    messages,
                    iterationOptions,
                    iteration + 1);
                totalUsage += usage;
                lastUsage = usage;

                if (iterationResult != null)
                {
                    result = iterationResult;
                    break;
                }
            }

            // 工具调用循环已结束（拿到最终回复或达到最大迭代次数）：统一评估压缩，
            // 为下一条用户消息腾出上下文。
            // 上下文占用 = 最后一次请求的 输入+输出（覆盖而非多轮累加）：
            // 工具多轮迭代中每一轮输入都包含完整上下文（重复计数），累加 totalUsage
            // 会虚高 N 倍，导致 ContextRatio 提前触达阈值而过度触发有损压缩；
            // 以最后一次请求的 total（prompt+completion）衡量：输出即将作为 assistant
            // 消息在下一轮重新计入输入，提前计入可让压缩略早触发——宁可压缩提前，
            // 不冒工具链上下文超出模型上限的风险。
            bool compacted = false;
            contextManager.context.TokenUsed = lastUsage.promptUsage + lastUsage.completionUsage;
            if (contextManager.ContextRatio >= options.ContextCompactRatio)
            {
                await Compact(cancellationToken, 0, null);
                compacted = true;
            }
            // 压缩后历史已由 Compact 替换、TokenUsed 已重置，无需重复写入
            if (!compacted && contextManager.contextHistory != null)
            {
                await contextManager.contextHistory.Append(messages);
            }
            // 模型未返回内容（空 content 且无工具调用）时给调用方一个明确的占位提示，
            // 避免上层表现为"无回复"
            var completedResult = result ?? "（模型未返回内容）";
            Log(new AgentLogEvent(
                AgentLogEventKind.ChatCompleted,
                DateTimeOffset.UtcNow,
                Result: completedResult,
                Usage: totalUsage));
            return (completedResult, totalUsage);
        }
        catch (Exception exception)
        {
            Log(new AgentLogEvent(
                AgentLogEventKind.ChatFailed,
                DateTimeOffset.UtcNow,
                Exception: exception));
            throw;
        }
    }

    /// <summary>
    /// 单次对话迭代与工具执行链路（RunIteration / InvokeToolAsync / TruncateToolResult）
    /// 已拆分至 Agent.RunIteration.cs 部分类。

    private void Log(AgentLogEvent logEvent)
    {
        try
        {
            options.OnLog?.Invoke(logEvent);
        }
        catch
        {
            // Diagnostics must never alter the Agent's normal execution path.
        }
    }

    /// <summary>best-effort 消息审计回调：异常被吞掉，消息记录绝不能影响对话主流程。</summary>
    private void RecordMessage(Message message, TokenUsage usage)
    {
        try
        {
            options.OnMessageRecorded?.Invoke(message, usage);
        }
        catch
        {
            // Message audit must never alter the Agent's normal execution path.
        }
    }

    /// <summary>手动触发上下文压缩（供 TUI /compact 与群聊 /compact 命令）。iteration=0 仅用于日志标注；topic 为空时全量通用压缩。</summary>
    public Task CompactAsync(CancellationToken cancellationToken, string? topic = null) => Compact(cancellationToken, 0, topic);

    /// <summary>清空当前会话上下文（内存消息 + 持久化历史）。供 TUI /new。</summary>
    public async Task ResetAsync()
    {
        contextManager.context.Messages = new List<Message>();
        contextManager.context.TokenUsed = 0;
        if (contextManager.contextHistory is not null)
        {
            await contextManager.contextHistory.Clear();
        }
    }


}
