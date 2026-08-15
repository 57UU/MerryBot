using System.Diagnostics.CodeAnalysis;
using CommonLib;
using LlmBackend;

namespace Agent.Session;

public class AgentSession
{
    /// <summary>消息队列上限：超出时丢弃最旧消息，保证最新消息不被阻塞</summary>
    private const int MaxQueued = 200;

    private readonly Agent _Agent;
    private readonly Action<string> _defaultMessageChannel;
    public TokenUsage SessionUsage = TokenUsage.Zero;

    public AgentSession(Agent agent, Action<string> defaultMessageChannel)
    {
        _Agent = agent;
        _defaultMessageChannel = defaultMessageChannel;
    }
    private readonly SemaphoreSlim _chatMutex = new(1, 1);

    /// <summary>是否正在处理消息（供 AgentSessionManager 空闲清理判断，会话不忙时才允许释放）</summary>
    internal bool IsBusy => _chatMutex.CurrentCount == 0;

    private long _lastActiveTicks = DateTime.UtcNow.Ticks;

    /// <summary>最近一次活动时间（UTC），由入队/处理刷新；供 AgentSessionManager 空闲淘汰判断</summary>
    public DateTime LastActiveUtc
    {
        get => new(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc);
    }

    private void MarkActive() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);
    private sealed class PendingMessage
    {
        public required string Type { get; init; }
        public required string Message { get; init; }
        public required Action<string> MessageChannel { get; init; }
        public required CancellationToken Token { get; init; }
        public TaskCompletionSource<(string Result, TokenUsage Usage)>? Completion { get; init; }
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
                if (MessageQueue.Count >= MaxQueued)
                {
                    // 队列已满：丢弃最旧的一条（其等待者按取消处理），保证最新消息不被阻塞
                    var oldest = MessageQueue.First!.Value;
                    MessageQueue.RemoveFirst();
                    oldest.Completion?.TrySetCanceled();
                }
                MessageQueue.AddLast(pending);
            }
        }
        MarkActive();
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
    /// 并返回 Agent 的最终文本与本次对话的 token 用量。这供 Cron 使用，使超时和执行日志对应实际执行。
    /// </summary>
    public Task<(string Result, TokenUsage Usage)> ChatAndWaitAsync(
        string message,
        Action<string>? messageChannel = null,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<(string Result, TokenUsage Usage)>(
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

    private async Task<(string Result, TokenUsage Usage)> DrainQueueAsync(PendingMessage first)
    {
        try
        {
            var (firstResult, firstUsage) = await Process(first.Message, first.MessageChannel, first.Token);
            first.Completion?.TrySetResult((firstResult, firstUsage));

            // 排空积压：用各自消息的 token，保证 FIFO。
            while (TryDequeue(out var pending))
            {
                try
                {
                    var (result, usage) = await Process(pending.Message, pending.MessageChannel, pending.Token);
                    pending.Completion?.TrySetResult((result, usage));
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

            return (firstResult, firstUsage);
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

    private async Task<(string Response, TokenUsage Usage)> Process(string message, Action<string> messageChannel, CancellationToken cancellationToken)
    {
        var (response, usage) = await _Agent.Chat(message, cancellationToken);
        SessionUsage += usage;
        MarkActive();
        try
        {
            messageChannel(response);
        }
        catch (Exception exception)
        {
            // 发送失败（如群消息发送异常）只记日志，不中断消息处理流程
            ConsoleLogger.Instance.Warn($"消息发送失败: {exception.Message}");
        }
        return (response, usage);
    }
}
