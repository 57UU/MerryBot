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

/// <summary>
/// Agent 会话控制服务：查询会话是否正忙、清除指定会话的上下文。
/// 由持有 <c>AgentSessionManager</c> 的 Agent 插件实现，供 WebUI 会话快照页使用。
/// </summary>
public interface IAgentSessionControlService
{
    /// <summary>指定 session 是否正在处理消息（排队中或生成中）。会话不存在时返回 false，且不会为查询创建会话。</summary>
    Task<bool> IsSessionBusyAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除指定 session 的上下文（内存消息 + 持久化历史，等价于群聊 <c>/new</c>），并重建会话以刷新工具集。
    /// 会话正忙时拒绝清除并返回 false；调用方应提示用户稍后再试。
    /// </summary>
    /// <returns>true=已清除；false=会话正忙，未清除。</returns>
    Task<bool> TryClearSessionAsync(string sessionKey, CancellationToken cancellationToken = default);
}
