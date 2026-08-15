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
    private async Task Compact(CancellationToken cancellationToken, int iteration)
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
                (token, context) => CompactContext(token, context, iteration));
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
        int iteration)
    {
        var forkedContext = context.Fork();
        forkedContext.Messages.Add(Message.User("请将上文压缩为一个段落，无需保留system prompt"));
        Log(new AgentLogEvent(AgentLogEventKind.ModelRequest, DateTimeOffset.UtcNow, iteration));
        var (result, tokenUsage) = await llmClient.Generate(cancellationToken, forkedContext.Messages, SystemPrompt, new LlmOptions());
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
                messages.Add(Message.User(userInput));
            }

            var toolDefs = toolSets!.SelectMany(toolSet => toolSet.Tools()).ToList();
            var llmOptions = new LlmOptions
            {
                Tools = toolDefs.Count > 0 ? toolDefs : null,
                MaxTokens = options.MaxOutputTokens,
                ReasoningEffort = options.ReasoningEffort,
            };

            TokenUsage totalUsage = TokenUsage.Zero;
            string? result = null;
            bool compacted = false;
            // 当前上下文累计用量，用于触发自动压缩；压缩后上下文只剩摘要，重置为 0
            int contextUsage = 0;

            // 对话循环：直到模型不再请求工具调用或达到最大迭代次数
            for (int iteration = 0; iteration < options.MaxIterations; iteration++)
            {
                // 最后一次迭代不提供工具，强迫模型直接返回文本输出，避免收尾失败；
                // 必须保留 ReasoningEffort：anthropic 开启 thinking 后历史含思考块，
                // 请求突然关闭 thinking 会被 API 拒绝（thinking 块必须持续回传）
                var iterationOptions = iteration == options.MaxIterations - 1
                    ? new LlmOptions(MaxTokens: options.MaxOutputTokens, ReasoningEffort: options.ReasoningEffort)
                    : llmOptions;

                TokenUsage usage;
                string? iterationResult;
                (usage, iterationResult) = await RunIteration(
                    cancellationToken,
                    messages,
                    iterationOptions,
                    iteration + 1);
                totalUsage += usage;
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
                    await Compact(cancellationToken, iteration + 1);
                    messages = contextManager.context.Messages;
                    compacted = true;
                    contextUsage = 0;
                }
            }

            // 压缩后历史已由 Compact 替换、TokenUsed 已重置，无需重复写入
            if (!compacted && contextManager.contextHistory != null)
            {
                contextManager.context.TokenUsed = contextUsage;
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
    /// 单次对话迭代：生成回复并回填工具调用结果。
    /// 返回本次用量与最终回复；result 为 null 表示模型请求了工具调用，还需继续迭代
    /// </summary>
    private async Task<(TokenUsage usage, string? result)> RunIteration(
        CancellationToken cancellationToken,
        IList<Message> messages,
        LlmOptions llmOptions,
        int iteration)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Log(new AgentLogEvent(AgentLogEventKind.ModelRequest, DateTimeOffset.UtcNow, iteration));
        var (response, usage) = await llmClient.Generate(cancellationToken, messages, SystemPrompt, llmOptions);
        Log(new AgentLogEvent(
            AgentLogEventKind.ModelResponse,
            DateTimeOffset.UtcNow,
            iteration,
            Result: response.Content,
            Usage: usage));

        // 记录 assistant 回复（含工具调用与 reasoning）
        string? assistantContent = response.Content;
        messages.Add(new Message
        {
            role = Role.Assistant,
            content = string.IsNullOrEmpty(assistantContent) ? [] : [new MessagePartText { text = assistantContent }],
            toolCalls = response.ToolCalls ?? [],
            reasoningContent = response.ReasoningContent ?? string.Empty,
            thinkingBlocks = response.ThinkingBlocks ?? string.Empty,
        });

        // 无工具调用说明回复完成
        if (response.ToolCalls is not { Length: > 0 })
        {
            return (usage, response.Content);
        }

        // 工具执行期间通过回调追加的内容（如图片用户消息）；
        // 工具并发执行，故用并发队列收集。
        var iterationAdds = new ConcurrentQueue<Message>();
        // 并发执行所有工具调用，结果按调用顺序作为 tool 消息回填
        string[] toolResults;
        try
        {
            toolResults = await Task.WhenAll(
                response.ToolCalls.Select(toolCall => InvokeToolAsync(cancellationToken, toolCall, iteration, iterationAdds.Enqueue)));
        }
        catch (OperationCanceledException)
        {
            // 会话取消：为全部未完成的工具调用回填"已取消"结果，避免消息列表留下
            // 悬空 tool_calls 导致后续请求被 API 拒绝（400），随后继续传播取消
            foreach (var toolCall in response.ToolCalls)
            {
                messages.Add(new Message
                {
                    role = Role.Tool,
                    toolCallId = toolCall.Id,
                    content = [new MessagePartText { text = $"{{\"error\": \"工具 {toolCall.Name} 已取消\"}}" }],
                });
            }
            throw;
        }
        for (int i = 0; i < response.ToolCalls.Length; i++)
        {
            messages.Add(new Message
            {
                role = Role.Tool,
                toolCallId = response.ToolCalls[i].Id,
                content = [new MessagePartText { text = toolResults[i] }],
            });
        }

        // 工具追加的内容排在 tool 结果消息之后，下一轮生成时即可见
        while (iterationAdds.TryDequeue(out var added))
        {
            messages.Add(added);
        }
        return (usage, null);
    }

    /// <summary>工具结果最大长度（字符），超出截断防止长文本/超大图片 base64 撑爆上下文</summary>
    private const int MaxToolResultLength = 8000;

    /// <summary>
    /// 按工具名在已注册的 ToolSet 中查找并执行，未注册的工具返回错误信息供模型纠正。
    /// 工具执行异常不回抛——转为 error JSON 回填（与 ToolSetBridge 策略统一），模型可自纠；
    /// 仅会话取消（OperationCanceledException）继续向上传播，由 RunIteration 统一回填取消结果。
    /// </summary>
    private async Task<string> InvokeToolAsync(
        CancellationToken cancellationToken,
        ToolCall toolCall,
        int iteration,
        Action<Message> onIterationAdd)
    {
        Log(new AgentLogEvent(
            AgentLogEventKind.ToolCallStarted,
            DateTimeOffset.UtcNow,
            iteration,
            toolCall.Name,
            toolCall.Id,
            toolCall.Arguments));
        try
        {
            foreach (var toolSet in toolSets!)
            {
                if (toolSet.Tools().Any(t => t.function?.name == toolCall.Name))
                {
                    var result = await toolSet.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
                    result = TruncateToolResult(result);
                    Log(new AgentLogEvent(
                        AgentLogEventKind.ToolCallCompleted,
                        DateTimeOffset.UtcNow,
                        iteration,
                        toolCall.Name,
                        toolCall.Id,
                        toolCall.Arguments,
                        result));
                    return result;
                }
            }
            var missingTool = $"{{\"error\": \"未找到工具: {toolCall.Name}\"}}";
            Log(new AgentLogEvent(
                AgentLogEventKind.ToolCallFailed,
                DateTimeOffset.UtcNow,
                iteration,
                toolCall.Name,
                toolCall.Id,
                toolCall.Arguments,
                missingTool));
            return missingTool;
        }
        catch (OperationCanceledException)
        {
            // 会话取消：继续传播（不转 error JSON），由 RunIteration 统一回填取消结果
            throw;
        }
        catch (Exception exception)
        {
            // 工具执行异常不回抛：转为截断/消毒后的 error JSON 回填，模型可自纠后重试；
            // OperationCanceledException 已被上方两个分支处理，不会到这里
            var errorResult = $"{{\"error\": {System.Text.Json.JsonSerializer.Serialize(exception.Message)}}}";
            Log(new AgentLogEvent(
                AgentLogEventKind.ToolCallFailed,
                DateTimeOffset.UtcNow,
                iteration,
                toolCall.Name,
                toolCall.Id,
                toolCall.Arguments,
                Exception: exception));
            return errorResult;
        }
    }

    private static string TruncateToolResult(string result)
    {
        if (string.IsNullOrEmpty(result) || result.Length <= MaxToolResultLength)
        {
            return result;
        }
        return result[..MaxToolResultLength] + "\n...[已截断]";
    }

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

    /// <summary>手动触发上下文压缩（供 TUI /compact）。iteration=0 仅用于日志标注。</summary>
    public Task CompactAsync(CancellationToken cancellationToken) => Compact(cancellationToken, 0);

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
