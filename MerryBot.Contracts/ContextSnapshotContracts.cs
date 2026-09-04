namespace MerryBot.Contracts;

/// <summary>
/// 供 WebUI 读取 Agent 当前内存上下文快照。与 ai_messages 审计日志不同，
/// 上下文快照随压缩/重置变化，反映 Agent 当前实际"看到"的对话内容。
/// </summary>
public interface IContextSnapshotService
{
    /// <summary>列出所有有上下文快照的 session，按最后更新时间倒序。</summary>
    Task<IReadOnlyList<ContextSnapshotSession>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>获取指定 session 的完整上下文快照；不存在时返回 null。</summary>
    Task<ContextSnapshotDetail?> GetSnapshotAsync(string sessionKey, CancellationToken cancellationToken = default);
}

/// <summary>session 级摘要：sessionKey、消息条数、最后更新时间。</summary>
public sealed record ContextSnapshotSession(string SessionKey, int MessageCount, DateTimeOffset UpdatedAtUtc);

/// <summary>单条上下文消息的展示 DTO。</summary>
public sealed record ContextMessageEntry(
    string Role,
    string Content,
    string ToolCallId,
    IReadOnlyList<ContextToolCallEntry> ToolCalls,
    string ReasoningContent);

public sealed record ContextToolCallEntry(string Name, string Arguments);

/// <summary>完整的上下文快照详情。</summary>
public sealed record ContextSnapshotDetail(
    string SessionKey,
    IReadOnlyList<ContextMessageEntry> Messages,
    DateTimeOffset UpdatedAtUtc);
