using LlmBackend;
using LlmClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agent;

public class AgentOptions
{
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public int MaxIterations { get; set; } = 20;
    public double ContextCompactRatio { get; set; } = 0.7;
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// 单轮迭代中并行执行的工具调用数上限（&gt;=1）。模型一次可能请求多个工具调用，
    /// 全部并发会放大成本与资源占用（如大量 web_fetch/后台任务）；超限部分排队串行执行。
    /// </summary>
    public int MaxConcurrentToolCalls { get; set; } = 4;

    /// <summary>
    /// 深度思考档位（"low" / "medium" / "high"），仅 anthropic 格式生效：
    /// 映射为 thinking budget_tokens，返回的思考块（含签名）在 tool calling
    /// 多轮中原样回传。空值表示不开启。
    /// </summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>
    /// Optional, best-effort lifecycle callback. Exceptions raised by the
    /// callback are ignored so observability can never interrupt a chat.
    /// </summary>
    public Action<AgentLogEvent>? OnLog { get; set; }

    /// <summary>
    /// Optional, best-effort message audit callback：每条对话消息（user/assistant/tool）
    /// 产生时回调，携带当轮 token 用量（user/tool 为 Zero）。回调内应自行过滤非文本 part；
    /// 抛出的异常会被忽略，不影响对话主流程。null 表示不记录。
    /// </summary>
    public Action<Message, TokenUsage>? OnMessageRecorded { get; set; }
}
