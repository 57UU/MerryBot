using System.Collections.Concurrent;
using LlmBackend;
using LlmClient;

namespace Agent;

/// <summary>
/// Agent 的部分类文件：单次对话迭代与工具执行链路（RunIteration / InvokeToolAsync）。
/// 拆分自 Agent.cs 以控制单文件规模；partial class 共享私有成员（llmClient/options/toolSets/Log 等）。
/// </summary>
public partial class Agent
{
    /// <summary>
    /// 单次对话迭代：生成回复并回填工具调用结果。
    /// 返回本次用量与最终回复；result 为 null 表示模型请求了工具调用，还需继续迭代。
    /// userInterruptToken：用户主动中断（如群聊 /stop）的独立 token，仅用于取消回填时区分
    /// "用户取消"与"超时/上游取消"；不参与取消传播（传播仍由 cancellationToken 承担）
    /// </summary>
    private async Task<(TokenUsage usage, string? result)> RunIteration(
        CancellationToken cancellationToken,
        IList<Message> messages,
        LlmOptions llmOptions,
        int iteration,
        CancellationToken userInterruptToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Log(new AgentLogEvent(AgentLogEventKind.ModelRequest, DateTimeOffset.UtcNow, iteration));

        // 流式生成：正文/推理增量以 ModelTextDelta/ModelReasoningDelta 事件实时上报
        // （供 UI 逐字渲染），segment 边界（start/reset）由 StreamCollector 解释；
        // 完整响应（正文/推理/工具调用/thinking 块）由 OnCompleted 承载，消息组装与原来一致
        var collector = new StreamCollector(this, iteration);
        await llmClient.GenerateStream(collector, messages, SystemPrompt, llmOptions, cancellationToken);
        var response = collector.Response
            // 流被取消/中断且未收到完成回调，无法组装助手消息；取消已由
            // OperationCanceledException 传播，走到这里说明是异常中断
            ?? throw new InvalidResponseException("模型流式响应中断，未收到完整结果");
        var usage = collector.Usage;

        // 记录 assistant 回复（含工具调用与 reasoning）
        string? assistantContent = response.Content;
        var assistantMessage = new Message
        {
            role = Role.Assistant,
            content = string.IsNullOrEmpty(assistantContent) ? [] : [new MessagePartText { text = assistantContent }],
            toolCalls = response.ToolCalls ?? [],
            reasoningContent = response.ReasoningContent ?? string.Empty,
            thinkingBlocks = response.ThinkingBlocks ?? string.Empty,
        };
        messages.Add(assistantMessage);
        RecordMessage(assistantMessage, usage);

        // 无工具调用说明回复完成
        if (response.ToolCalls is not { Length: > 0 })
        {
            return (usage, response.Content);
        }

        // 工具执行期间通过回调追加的内容（如图片用户消息）；
        // 工具并发执行，故用并发队列收集。
        var iterationAdds = new ConcurrentQueue<Message>();
        // 并发执行所有工具调用，但受 MaxConcurrentToolCalls 上限约束（防 LLM 一次请求大量并发工具
        // 导致资源/成本失控）；超限的工具排队串行执行。结果按调用顺序作为 tool 消息回填
        var maxConcurrent = Math.Max(1, options.MaxConcurrentToolCalls);
        using var gate = new SemaphoreSlim(maxConcurrent);
        var toolResults = new string[response.ToolCalls.Length];
        try
        {
            var tasks = response.ToolCalls.Select(async (toolCall, index) =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    toolResults[index] = await InvokeToolAsync(cancellationToken, toolCall, iteration, iterationAdds.Enqueue);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // 会话取消：为全部未完成的工具调用回填"已取消"结果，避免消息列表留下
            // 悬空 tool_calls 导致后续请求被 API 拒绝（400），随后继续传播取消。
            // 按 userInterruptToken 区分用户主动中断（/stop）与超时/上游取消
            var reason = userInterruptToken.IsCancellationRequested ? "用户取消" : "对话已中断（任务超时或上游取消）";
            foreach (var toolCall in response.ToolCalls)
            {
                var cancelledTool = new Message
                {
                    role = Role.Tool,
                    toolCallId = toolCall.Id,
                    content = [new MessagePartText { text = $"{{\"error\": \"{reason}，工具 {toolCall.Name} 已中止\"}}" }],
                };
                messages.Add(cancelledTool);
                RecordMessage(cancelledTool, TokenUsage.Zero);
            }
            throw;
        }
        for (int i = 0; i < response.ToolCalls.Length; i++)
        {
            var toolMessage = new Message
            {
                role = Role.Tool,
                toolCallId = response.ToolCalls[i].Id,
                content = [new MessagePartText { text = toolResults[i] }],
            };
            messages.Add(toolMessage);
            RecordMessage(toolMessage, TokenUsage.Zero);
        }

        // 工具追加的内容排在 tool 结果消息之后，下一轮生成时即可见
        while (iterationAdds.TryDequeue(out var added))
        {
            messages.Add(added);
            RecordMessage(added, TokenUsage.Zero);
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

    /// <summary>
    /// 流式消费的 segment 解释器：Client 只提供 delta/reset/completed 回调，
    /// "段"的边界在这里解释——首个增量到达时发 ModelStreamSegmentStart（attempt 从 1 起），
    /// OnReset 时发 ModelStreamSegmentReset 并推进 attempt；UI 据此丢弃作废段的渲染。
    /// </summary>
    private sealed class StreamCollector(Agent agent, int iteration) : IResettableStreamSink
    {
        private int _attempt = 1;
        private bool _segmentStarted;

        public GenerateResponse? Response { get; private set; }
        public TokenUsage Usage { get; private set; } = TokenUsage.Zero;

        public void OnTextDelta(string delta)
        {
            EnsureSegmentStarted();
            if (delta.Length > 0)
            {
                agent.Log(new AgentLogEvent(
                    AgentLogEventKind.ModelTextDelta, DateTimeOffset.UtcNow, iteration, Result: delta));
            }
        }

        public void OnReasoningDelta(string delta)
        {
            EnsureSegmentStarted();
            if (delta.Length > 0)
            {
                agent.Log(new AgentLogEvent(
                    AgentLogEventKind.ModelReasoningDelta, DateTimeOffset.UtcNow, iteration, Result: delta));
            }
        }

        public void OnReset(StreamResetReason reason, Exception cause)
        {
            agent.Log(new AgentLogEvent(
                AgentLogEventKind.ModelStreamSegmentReset, DateTimeOffset.UtcNow, iteration,
                Result: reason.ToString(), Exception: cause));
            _attempt++;
            _segmentStarted = false;
        }

        public void OnCompleted(GenerateResponse response, TokenUsage usage)
        {
            Response = response;
            Usage = usage;
        }

        private void EnsureSegmentStarted()
        {
            if (_segmentStarted)
            {
                return;
            }
            _segmentStarted = true;
            agent.Log(new AgentLogEvent(
                AgentLogEventKind.ModelStreamSegmentStart, DateTimeOffset.UtcNow, iteration,
                Result: _attempt.ToString()));
        }
    }
}