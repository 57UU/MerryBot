using LlmBackend;

namespace Agent;

/// <summary>Lifecycle stages emitted by <see cref="Agent"/> while processing a chat.</summary>
public enum AgentLogEventKind
{
    ChatStarted,
    ChatCompleted,
    ChatFailed,
    ModelRequest,
    ModelResponse,
    /// <summary>流式生成中的正文增量（高频事件，Result 为单个增量文本），供 UI 逐字渲染</summary>
    ModelTextDelta,
    /// <summary>流式生成中的推理增量（高频事件，Result 为单个增量文本），UI 默认不渲染</summary>
    ModelReasoningDelta,
    /// <summary>一个流式 segment 开始（一次模型尝试的首个增量；Result 为 attempt 序号）。
    /// segment 边界由 Agent 解释：ModelRequest 之后或 ModelStreamSegmentReset 之后的
    /// 增量属于新 segment。</summary>
    ModelStreamSegmentStart,
    /// <summary>当前 segment 作废（Client 将重建流重试），Exception 携带失败原因；
    /// UI 应丢弃该 segment 已渲染的全部增量</summary>
    ModelStreamSegmentReset,
    ContextCompaction,
    ToolCallStarted,
    ToolCallCompleted,
    ToolCallFailed,
}

/// <summary>
/// A best-effort diagnostic event raised by an <see cref="Agent"/>.
/// Iteration is one-based for model/tool work and zero for chat-level events.
/// </summary>
public sealed record AgentLogEvent(
    AgentLogEventKind Kind,
    DateTimeOffset TimestampUtc,
    int Iteration = 0,
    string? ToolName = null,
    string? ToolCallId = null,
    string? Arguments = null,
    string? Result = null,
    TokenUsage? Usage = null,
    Exception? Exception = null);
