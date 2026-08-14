using LlmBackend;
using LlmClient;
using System.Collections.Concurrent;
using System.Text;

namespace Agent;

public class Agent
{
    private ContextManager contextManager;
    private Client llmClient;
    private AgentOptions options;
    private IList<ToolSet>? toolSets;
    //--generate--
    public string SystemPrompt { get; internal set; }
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
        ContextHistory contextHistory,
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
    public Task Compact(CancellationToken cancellationToken)
    {
        return contextManager.Compact(cancellationToken, CompactContext);

    }
    private async Task<(string result, TokenUsage tokenUsage)> CompactContext(CancellationToken cancellationToken, Context context)
    {
        var forkedContext = context.Fork();
        forkedContext.Messages.Add(Message.User("请将上文压缩为一个段落，无需保留system prompt"));
        var (result, tokenUsage) = await llmClient.Generate(cancellationToken, forkedContext.Messages, SystemPrompt, new LlmOptions());
        return (result.Content!, tokenUsage);
    }

    public async Task<(string result, TokenUsage tokenUsage)> Chat(
        string userInput,
        CancellationToken cancellationToken)
    {
        var messages = contextManager.context.Messages;
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            messages.Add(Message.User(userInput));
        }

        var toolDefs = toolSets!.SelectMany(toolSet => toolSet.Tools()).ToList();
        var llmOptions = new LlmOptions
        {
            Tools = toolDefs.Count > 0 ? toolDefs : null,
        };

        int totalUsage = 0, promptUsage = 0, completionUsage = 0, cachedUsage = 0;
        string? result = null;
        bool compacted = false;
        // 当前上下文累计用量，用于触发自动压缩；压缩后上下文只剩摘要，重置为 0
        int contextUsage = 0;

        // 对话循环：直到模型不再请求工具调用或达到最大迭代次数
        for (int iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            // 最后一次迭代不提供工具，强迫模型直接返回文本输出，避免收尾失败
            var iterationOptions = iteration == options.MaxIterations - 1
                ? new LlmOptions()
                : llmOptions;

            var (usage, iterationResult) = await RunIteration(cancellationToken, messages, iterationOptions);
            totalUsage += usage.totalUsage;
            promptUsage += usage.promptUsage;
            completionUsage += usage.completionUsage;
            cachedUsage += usage.cachedUsage;
            contextUsage += usage.totalUsage;

            if (iterationResult != null)
            {
                result = iterationResult;
                break;
            }

            // 上下文占用达到阈值时自动压缩；压缩会替换消息列表并重置用量，需重新获取
            contextManager.context.TokenUsed = contextUsage;
            if (contextManager.ContextRatio >= options.ContextCompactRatio)
            {
                await Compact(cancellationToken);
                messages = contextManager.context.Messages;
                compacted = true;
                contextUsage = 0;
            }
        }

        // 压缩后历史已由 Compact 替换、TokenUsed 已重置，无需重复写入
        if (!compacted)
        {
            contextManager.context.TokenUsed = contextUsage;
            await contextManager.contextHistory.Append(messages);
        }
        return (result ?? string.Empty, new TokenUsage(totalUsage, promptUsage, completionUsage, cachedUsage));
    }

    /// <summary>
    /// 单次对话迭代：生成回复并回填工具调用结果。
    /// 返回本次用量与最终回复；result 为 null 表示模型请求了工具调用，还需继续迭代
    /// </summary>
    private async Task<(TokenUsage usage, string? result)> RunIteration(
        CancellationToken cancellationToken,
        IList<Message> messages,
        LlmOptions llmOptions)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (response, usage) = await llmClient.Generate(cancellationToken, messages, SystemPrompt, llmOptions);

        // 记录 assistant 回复（含工具调用与 reasoning）
        string? assistantContent = response.Content;
        messages.Add(new Message
        {
            role = Role.Assistant,
            content = string.IsNullOrEmpty(assistantContent) ? [] : [new MessagePartText { text = assistantContent }],
            toolCalls = response.ToolCalls ?? [],
            reasoningContent = response.ReasoningContent ?? string.Empty,
        });

        // 无工具调用说明回复完成
        if (response.ToolCalls is not { Length: > 0 })
        {
            return (usage, response.Content);
        }

        // 工具执行期间通过 OnIterationAdd 回调追加的内容（如图片用户消息），
        // 工具并发执行，故用并发队列收集
        var iterationAdds = new ConcurrentQueue<Message>();
        foreach (var toolSet in toolSets!)
        {
            toolSet.OnIterationAdd = iterationAdds.Enqueue;
        }
        try
        {
            // 并发执行所有工具调用，结果按调用顺序作为 tool 消息回填
            var toolResults = await Task.WhenAll(
                response.ToolCalls.Select(toolCall => InvokeToolAsync(cancellationToken, toolCall)));
            for (int i = 0; i < response.ToolCalls.Length; i++)
            {
                messages.Add(new Message
                {
                    role = Role.Tool,
                    toolCallId = response.ToolCalls[i].Id,
                    content = [new MessagePartText { text = toolResults[i] }],
                });
            }
        }
        finally
        {
            foreach (var toolSet in toolSets!)
            {
                toolSet.OnIterationAdd = null;
            }
        }

        // 工具追加的内容排在 tool 结果消息之后，下一轮生成时即可见
        while (iterationAdds.TryDequeue(out var added))
        {
            messages.Add(added);
        }
        return (usage, null);
    }

    /// <summary>
    /// 按工具名在已注册的 ToolSet 中查找并执行，未注册的工具返回错误信息供模型纠正
    /// </summary>
    private async Task<string> InvokeToolAsync(CancellationToken cancellationToken, ToolCall toolCall)
    {
        foreach (var toolSet in toolSets!)
        {
            if (toolSet.Tools().Any(t => t.function?.name == toolCall.Name))
            {
                return await toolSet.InvokeAsync(cancellationToken, toolCall);
            }
        }
        return $"{{\"error\": \"未找到工具: {toolCall.Name}\"}}";
    }


}
