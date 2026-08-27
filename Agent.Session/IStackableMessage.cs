using LlmBackend;

namespace Agent.Session;

/// <summary>
/// 队列节点：承载一个或多个内容块，支持追加、按 task_id 撤回、序列化。
/// 信封（Channel/Token/Completion）由 EnqueueStackable 的 onCreate 工厂提供，内容块由 Append 统一追加；
/// 同一 type 的后续入队走 Append 合并为单节点。
/// </summary>
public interface IStackableMessage
{
    /// <summary>追加一块内容（同 type 合并时调用）；messageId 用于后续按 task_id 撤回，普通消息为 null</summary>
    void Append(string? messageId, string content);

    /// <summary>按 messageId 撤回一块内容；块不存在时为幂等 noop</summary>
    void Delete(string messageId);

    /// <summary>块撤空后为 true，节点整体移除</summary>
    bool IsEmpty { get; }

    /// <summary>合并为最终喂给 LLM 的文本</summary>
    string Build();

    /// <summary>消息发送通道（执行时由 Process 取用；可空，空时回落到会话默认通道）</summary>
    Action<string>? Channel { get; }

    /// <summary>取消令牌（执行时由 Process 取用）</summary>
    CancellationToken Token { get; }

    /// <summary>完成通知（ChatAndWaitAsync 等待方取用；通知类消息为 null）</summary>
    TaskCompletionSource<(string Result, TokenUsage Usage)>? Completion { get; }
}

/// <summary>IStackableMessage 的默认实现：信封在构造时确定，内容块经 Append 累积。</summary>
public sealed class StackableMessage : IStackableMessage
{
    private readonly List<(string? TaskId, string Content)> _blocks = new();

    public Action<string>? Channel { get; }
    public CancellationToken Token { get; }
    public TaskCompletionSource<(string Result, TokenUsage Usage)>? Completion { get; }

    public StackableMessage(
        Action<string>? channel,
        CancellationToken token,
        TaskCompletionSource<(string Result, TokenUsage)>? completion)
    {
        Channel = channel;
        Token = token;
        Completion = completion;
    }

    public void Append(string? messageId, string content) => _blocks.Add((messageId, content));

    public void Delete(string messageId) => _blocks.RemoveAll(block => block.TaskId == messageId);

    public bool IsEmpty => _blocks.Count == 0;

    public string Build() => string.Join("\n", _blocks.Select(block => block.Content));
}
