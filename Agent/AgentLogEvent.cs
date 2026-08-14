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
