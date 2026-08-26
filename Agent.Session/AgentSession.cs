using CommonLib;
using LlmBackend;

namespace Agent.Session;

public class AgentSession
{
    /// <summary>消息队列上限：超出时丢弃最旧消息，保证最新消息不被阻塞</summary>
    private const int MaxQueued = 200;

    private readonly Agent _Agent;
    private readonly Action<string> _defaultMessageChannel;
    private readonly ISimpleLogger _logger;
    public TokenUsage SessionUsage = TokenUsage.Zero;

    public AgentSession(Agent agent, Action<string> defaultMessageChannel, ISimpleLogger? logger = null)
    {
        _Agent = agent;
        _defaultMessageChannel = defaultMessageChannel;
        _logger = logger ?? SimpleLog.Default;
    }
    /// <summary>
    /// 是否正在处理消息（供 AgentSessionManager 空闲清理判断，会话不忙时才允许释放）。
    /// 即排空循环是否在运行：从首条消息入队启动排空，到最后一次判空退出期间为 true。
    /// </summary>
    internal bool IsBusy => _draining;

    /// <summary>当前正在处理的消息对应的取消源（链接消息自带 token），供 /stop 取消本轮对话；null 表示空闲</summary>
    private CancellationTokenSource? _activeCts;
    private readonly object _ctsLock = new();

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
    // 用 LinkedList + 单一 _gate 锁实现，支持"合并同类型 stackable 消息"（正文拼接，不限队尾）。
    //
    // 调度不变式：入队方"追加/合并 + 检查是否需要启动排空"与排空方"判空 + 复位 _draining"
    // 在同一把 _gate 锁内原子完成，保证任何入队的消息要么被运行中的排空循环消费、
    // 要么触发新的排空循环。旧实现（SemaphoreSlim Wait(0) 失败后再入队）存在竞态窗口：
    // 排空循环恰好在最后一次判空之后、释放信号量之前退出，新消息入队后无任何消费者，
    // 会卡死到下一条消息到来（且届时反序处理），或随会话空闲淘汰静默丢失。
    private readonly object _gate = new();
    private readonly LinkedList<PendingMessage> MessageQueue = new();
    /// <summary>排空循环运行标志：true 表示有且仅有一个消费者在排空队列</summary>
    private volatile bool _draining;

    /// <summary>
    /// 入队。stackable 消息与队列中已有的同类型节点合并（正文拼接，收敛为每类型至多一个节点）：
    /// 后台任务通知（subagent_result / task_result）的各块自含 task_id/status/output，
    /// 拼接后模型一轮即可处理全部结果，既防积压也不丢内容（旧"替换队尾"语义会静默丢弃被覆盖的通知，
    /// 且 subagent_output 查全文仅有 5 分钟保留期）。合并不限队尾——队列中同类型节点与异类消息
    /// 交错时（群消息批 wait 与通知交替入队是常态）同样收敛。
    /// 非 stackable、或队列中无同类型节点时追加到队尾。会话空闲（无排空循环）时启动排空。
    /// </summary>
    private void Enqueue(PendingMessage pending, bool stackable)
    {
        bool startDrain = false;
        lock (_gate)
        {
            bool merged = false;
            if (stackable)
            {
                // FIFO 下队首方向第一个同类型节点即最旧；合并进它的原位置，
                // 维持不变式"每类型至多一个节点"，新通知的结果在时间线上随首个同类通知交付
                for (var node = MessageQueue.First; node != null; node = node.Next)
                {
                    if (node.Value.Type != pending.Type)
                    {
                        continue;
                    }
                    node.Value = new PendingMessage
                    {
                        Type = node.Value.Type,
                        Message = node.Value.Message + "\n" + pending.Message,
                        // 通道/Token/完成通知沿用原节点：stackable 调用方（后台任务完成通知）
                        // 不携带自定义通道与等待者，两者实际取值一致
                        MessageChannel = node.Value.MessageChannel,
                        Token = node.Value.Token,
                        Completion = node.Value.Completion,
                    };
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                if (MessageQueue.Count >= MaxQueued)
                {
                    // 队列已满：丢弃最旧的一条（其等待者按取消处理），保证最新消息不被阻塞
                    var oldest = MessageQueue.First!.Value;
                    MessageQueue.RemoveFirst();
                    oldest.Completion?.TrySetCanceled();
                }
                MessageQueue.AddLast(pending);
                // 合并路径无需检查：队列已有同类型节点 ⇒ 排空循环必然在运行
                //（_draining 仅在锁内队列判空时复位）
                if (!_draining)
                {
                    _draining = true;
                    startDrain = true;
                }
            }
        }
        if (startDrain)
        {
            // fire-and-forget：排空循环自身吞掉并记录所有异常，绝不外抛
            _ = DrainQueueAsync();
        }
        MarkActive();
    }

    /// <summary>
    /// 入队一条消息并立即返回（不等待处理完成）。排空由会话自动调度：
    /// 空闲时入队即启动排空循环，忙时由运行中的排空循环按 FIFO 消费。
    /// </summary>
    public Task Chat(string message, Action<string>? messageChannel = null, string type = "default", bool stackable = false, CancellationToken cancellationToken = default)
    {
        var pending = new PendingMessage
        {
            Message = message,
            MessageChannel = messageChannel ?? _defaultMessageChannel,
            Token = cancellationToken,
            Type = type,
        };
        Enqueue(pending, stackable);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 与 Chat 的区别是：会等待排队消息真正执行完成，
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
            // 使用保留类型 wait，避免被 stackable 的同类消息合并（wait 等待方需要独立的完成通知）
            Type = "wait",
            Message = message,
            MessageChannel = messageChannel ?? _defaultMessageChannel,
            Token = cancellationToken,
            Completion = completion,
        };
        Enqueue(pending, stackable: false);
        return completion.Task;
    }

    /// <summary>
    /// 排空队列（会话唯一消费者，fire-and-forget）：逐条用各自消息的 token 执行，保证 FIFO。
    /// 单条消息失败或取消只终止该条（完成通知对应传播），不中断排空。
    /// 退出条件：队列为空——在 _gate 内判空并复位 _draining，与 Enqueue 的启动检查原子互斥，
    /// 保证入队的消息不存在"无消费者"的窗口。
    /// </summary>
    private async Task DrainQueueAsync()
    {
        while (true)
        {
            PendingMessage pending;
            lock (_gate)
            {
                var first = MessageQueue.First;
                if (first == null)
                {
                    _draining = false;
                    return;
                }
                pending = first.Value;
                MessageQueue.RemoveFirst();
            }

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
                // 无等待者的消息（如 subagent/终端通知）：排空循环吞掉异常前必须留痕，
                // 否则 LLM 失败等信息完全静默
                if (pending.Completion == null)
                {
                    _logger.Error($"会话消息处理失败: {ex.Message}");
                }
                pending.Completion?.TrySetException(ex);
            }
        }
    }

    private async Task<(string Response, TokenUsage Usage)> Process(string message, Action<string> messageChannel, CancellationToken cancellationToken)
    {
        // 两个取消源分工：
        //   upstreamCts —— 链接消息自带 token（插件关闭 disposeCts、Cron 任务超时等上游取消）
        //   stopCts     —— 登记为 _activeCts，仅由 /stop（Stop()）触发，代表用户主动中断
        // workCts 取两者并集驱动实际执行；Chat 同时拿到 stopCts.Token 作为 userInterruptToken，
        // 使工具回填处能区分"用户取消"与"超时/上游取消"
        using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopCts = new CancellationTokenSource();
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(upstreamCts.Token, stopCts.Token);
        lock (_ctsLock)
        {
            _activeCts = stopCts;
        }
        try
        {
            var (response, usage) = await _Agent.Chat(message, workCts.Token, stopCts.Token);
            SessionUsage += usage;
            MarkActive();
            try
            {
                messageChannel(response);
            }
            catch (Exception exception)
            {
                // 发送失败（如群消息发送异常）只记日志，不中断消息处理流程
                _logger.Warn($"消息发送失败: {exception.Message}");
            }
            return (response, usage);
        }
        finally
        {
            lock (_ctsLock)
            {
                if (ReferenceEquals(_activeCts, stopCts))
                {
                    _activeCts = null;
                }
            }
            stopCts.Dispose();
        }
    }

    /// <summary>
    /// 停止当前正在处理的对话并丢弃积压队列。
    /// 返回是否有正在处理的对话被取消。
    /// </summary>
    public bool Stop()
    {
        bool cancelled;
        lock (_ctsLock)
        {
            cancelled = _activeCts != null;
            _activeCts?.Cancel();
        }
        // 丢弃积压消息，等待者（如 Cron 的 ChatAndWaitAsync）按取消处理
        lock (_gate)
        {
            while (MessageQueue.First != null)
            {
                var pending = MessageQueue.First.Value;
                MessageQueue.RemoveFirst();
                pending.Completion?.TrySetCanceled();
            }
        }
        return cancelled;
    }

    /// <summary>清空当前会话上下文（内存消息 + 持久化历史）。供 TUI /new。</summary>
    public Task ResetAsync() => _Agent.ResetAsync();

    /// <summary>手动触发上下文压缩（topic 为空时全量通用压缩）。供 TUI /compact 与群聊 /compact 命令。</summary>
    public Task CompactAsync(CancellationToken cancellationToken, string? topic = null) => _Agent.CompactAsync(cancellationToken, topic);
}
