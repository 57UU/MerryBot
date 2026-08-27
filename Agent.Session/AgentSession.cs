using CommonLib;
using LlmBackend;
using System.Collections.Generic;

namespace Agent.Session;

public class AgentSession
{
    /// <summary>消息队列上限：超出时丢弃最旧消息，保证最新消息不被阻塞</summary>
    private const int MaxQueued = 200;

    private readonly Agent _Agent;
    private readonly Action<string> _defaultMessageChannel;
    private readonly ISimpleLogger _logger;
    public TokenUsage SessionUsage = TokenUsage.Zero;

    /// <summary>非 stackable 入队用的自增序号，保证每条 Chat/ChatAndWaitAsync 获得唯一 key、各自成节点</summary>
    private int _seq;

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

    // 队列：有序字典，key 为 type（stackable 通知按 type 合并为单节点；普通 Chat/ChatAndWaitAsync
    // 用 type#序号 唯一 key，各自成节点、保持 FIFO 与各自的完成通知）。value 为 IStackableMessage，
    // 承载信封（Channel/Token/Completion）与一个或多个内容块。
    //
    // 调度不变式：入队方"新增/合并 + 检查是否启动排空"与排空方"判空 + 复位 _draining"
    // 在同一把 _gate 锁内原子完成，保证任何入队的消息要么被运行中的排空循环消费、
    // 要么触发新的排空循环。
    private readonly object _gate = new();
    private readonly OrderedDictionary<string, IStackableMessage> _queue = new();
    /// <summary>排空循环运行标志：true 表示有且仅有一个消费者在排空队列</summary>
    private volatile bool _draining;

    /// <summary>
    /// 唯一入队入口。type 为合并键：同 type 已存在则把内容块追加进已有节点（合并）；
    /// 不存在则经 onCreate 工厂建信封并追加首块。
    /// </summary>
    /// <returns>true=已存在同 type 节点并合并（即已经在排队）；false=新建节点</returns>
    public bool EnqueueStackable(string type, string? messageId, string content, Func<IStackableMessage> onCreate)
    {
        bool merged, startDrain = false;
        lock (_gate)
        {
            if (_queue.TryGetValue(type, out var existing))
            {
                existing.Append(messageId, content); // 合并：仅追加内容块
                merged = true;
            }
            else
            {
                if (_queue.Count >= MaxQueued)
                {
                    // 队列已满：丢弃最旧的一条（其等待者按取消处理），保证最新消息不被阻塞
                    var oldest = _queue.First();
                    _queue.Remove(oldest.Key);
                    oldest.Value.Completion?.TrySetCanceled();
                }
                var msg = onCreate();            // 工厂建信封（Channel/Token/Completion）
                msg.Append(messageId, content);  // 追加首块
                _queue[type] = msg;
                merged = false;
            }
            if (!_draining)
            {
                _draining = true;
                startDrain = true;
            }
        }
        if (startDrain)
        {
            // fire-and-forget：排空循环自身吞掉并记录所有异常，绝不外抛
            _ = DrainQueueAsync();
        }
        MarkActive();
        return merged;
    }

    /// <summary>
    /// 撤回已入队的某 type 下指定 messageId 的结果块（模型用 task_output/subagent_output 拉取全文后调用，
    /// 避免同一结果经"推送"与"拉取"双通道重复投递给模型）。块撤空则节点整体移除；
    /// 块不存在（尚未推送或已投递）时为幂等 noop。
    /// </summary>
    public void RemoveQueued(string type, string messageId)
    {
        lock (_gate)
        {
            if (_queue.TryGetValue(type, out var entry))
            {
                entry.Delete(messageId);
                if (entry.IsEmpty)
                {
                    _queue.Remove(type);
                }
            }
        }
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
        EnqueueStackable("wait#" + Interlocked.Increment(ref _seq), null, message,
            () => new StackableMessage(messageChannel ?? _defaultMessageChannel, cancellationToken, completion));
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
            IStackableMessage message;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _draining = false;
                    return;
                }
                var first = _queue.First();
                message = first.Value;
                _queue.Remove(first.Key);
            }

            try
            {
                var (result, usage) = await Process(message.Build(), message.Channel ?? _defaultMessageChannel, message.Token);
                message.Completion?.TrySetResult((result, usage));
            }
            catch (OperationCanceledException) when (message.Token.IsCancellationRequested)
            {
                message.Completion?.TrySetCanceled(message.Token);
            }
            catch (Exception ex)
            {
                // 无等待者的消息（如 subagent/终端通知）：排空循环吞掉异常前必须留痕，
                // 否则 LLM 失败等信息完全静默
                if (message.Completion == null)
                {
                    _logger.Error($"会话消息处理失败: {ex.Message}");
                }
                message.Completion?.TrySetException(ex);
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
            foreach (var entry in _queue)
            {
                entry.Value.Completion?.TrySetCanceled();
            }
            _queue.Clear();
        }
        return cancelled;
    }

    /// <summary>清空当前会话上下文（内存消息 + 持久化历史）。供 TUI /new。</summary>
    public Task ResetAsync() => _Agent.ResetAsync();

    /// <summary>手动触发上下文压缩（topic 为空时全量通用压缩）。供 TUI /compact 与群聊 /compact 命令。</summary>
    public Task CompactAsync(CancellationToken cancellationToken, string? topic = null) => _Agent.CompactAsync(cancellationToken, topic);
}
