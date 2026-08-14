using System.Diagnostics.CodeAnalysis;
using LlmBackend;

namespace Agent.Session;

public class AgentSession
{
    private readonly Agent _Agent;
    private readonly Action<string> _defaultMessageChannel;
    public TokenUsage SessionUsage = TokenUsage.Zero;

    public AgentSession(Agent agent, Action<string> defaultMessageChannel)
    {
        _Agent = agent;
        _defaultMessageChannel = defaultMessageChannel;
    }
    private readonly SemaphoreSlim _chatMutex = new(1, 1);
    public bool Busy => _chatMutex.CurrentCount == 0;
    private sealed class PendingMessage
    {
        public required string Type { get; init; }
        public required string Message { get; init; }
        public required Action<string> MessageChannel { get; init; }
        public required CancellationToken Token { get; init; }
        public TaskCompletionSource<string>? Completion { get; init; }
    }

    // 队列：随消息保存各自的 CancellationToken 和可选完成通知。
    // 用 LinkedList + 锁实现，支持"合并连续同类消息"（stackable 时替换队尾）。
    private readonly object _queueLock = new();
    private readonly LinkedList<PendingMessage> MessageQueue = new();

    /// <summary>
    /// 入队。stackable 且队尾存在同类型消息时，替换队尾为当前消息（合并连续同类消息，避免积压）；
    /// 否则追加到队尾。
    /// </summary>
    private void Enqueue(PendingMessage pending, bool stackable)
    {
        lock (_queueLock)
        {
            var last = MessageQueue.Last;
            if (stackable && last != null && last.Value.Type == pending.Type)
            {
                last.Value = pending;
            }
            else
            {
                MessageQueue.AddLast(pending);
            }
        }
    }

    private bool TryDequeue([MaybeNullWhen(false)] out PendingMessage pending)
    {
        lock (_queueLock)
        {
            var first = MessageQueue.First;
            if (first == null)
            {
                pending = null;
                return false;
            }
            pending = first.Value;
            MessageQueue.RemoveFirst();
            return true;
        }
    }

    public async Task Chat(string message, Action<string>? messageChannel = null, string type = "default", bool stackable = false, CancellationToken cancellationToken = default)
    {
        var pending = new PendingMessage
        {
            Message = message,
            MessageChannel = messageChannel ?? _defaultMessageChannel,
            Token = cancellationToken,
            Type = type,
        };

        if (_chatMutex.Wait(0))
        {
            await DrainQueueAsync(pending);
        }
        else
        {
            // busy，入队后立即返回，不阻塞调用方；stackable 且队尾同类时替换队尾
            Enqueue(pending, stackable);
        }
    }

    /// <summary>
    /// 与 Chat 的区别是：如果当前会话忙，会等待排队消息真正执行完成，
    /// 并返回 Agent 的最终文本。这供 Cron 使用，使超时和执行日志对应实际执行。
    /// </summary>
    public Task<string> ChatAndWaitAsync(
        string message,
        Action<string>? messageChannel = null,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingMessage
        {
            // 使用保留类型 wait，避免被 stackable 的同类消息替换队尾
            Type = "wait",
            Message = message,
            MessageChannel = messageChannel ?? _defaultMessageChannel,
            Token = cancellationToken,
            Completion = completion,
        };

        if (_chatMutex.Wait(0))
        {
            return DrainQueueAsync(pending);
        }

        Enqueue(pending, stackable: false);
        return completion.Task;
    }

    private async Task<string> DrainQueueAsync(PendingMessage first)
    {
        try
        {
            var firstResult = await Process(first.Message, first.MessageChannel, first.Token);
            first.Completion?.TrySetResult(firstResult);

            // 排空积压：用各自消息的 token，保证 FIFO。
            while (TryDequeue(out var pending))
            {
                try
                {
                    var result = await Process(pending.Message, pending.MessageChannel, pending.Token);
                    pending.Completion?.TrySetResult(result);
                }
                catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
                {
                    pending.Completion?.TrySetCanceled(pending.Token);
                }
                catch (Exception ex)
                {
                    pending.Completion?.TrySetException(ex);
                }
            }

            return firstResult;
        }
        catch (Exception ex)
        {
            first.Completion?.TrySetException(ex);
            throw;
        }
        finally
        {
            // 无论成功还是异常都释放锁，避免永久卡在 busy 状态
            _chatMutex.Release();
        }
    }

    private async Task<string> Process(string message, Action<string> messageChannel, CancellationToken cancellationToken)
    {
        var (response, usage) = await _Agent.Chat(message, cancellationToken);
        SessionUsage += usage;
        messageChannel(response);
        return response;
    }
}
