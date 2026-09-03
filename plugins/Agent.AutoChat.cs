using System.Collections.Concurrent;
using Agent.Session;
using NapcatClient.MessageType;

namespace BotPlugin;

/// <summary>
/// 自动水群配置快照：会话创建时确定该会话是否为 auto 模式。
/// null 表示非 auto 模式（MessageTool 不注册 send_message，行为与原来一致）；
/// 非 null 表示 auto 模式，send_message 作为唯一发送口并受配额与 DryRun 约束。
/// </summary>
public sealed class AutoChatSettings
{
    public required bool DryRun { get; init; }
    public required AutoChatSendBudget Budget { get; init; }
}

/// <summary>
/// 单批发送配额：由投递侧在每轮水群触发前后 BeginRound/EndRound，send_message 凭 TryAcquire 发送。
/// 非水群轮次（@ 对话）不限次，避免误伤正常工具链。
/// </summary>
public sealed class AutoChatSendBudget
{
    private readonly Lock syncRoot = new();
    private int remaining;
    private bool inRound;

    public void BeginRound(int limit)
    {
        lock (syncRoot)
        {
            remaining = Math.Max(0, limit);
            inRound = true;
        }
    }

    public void EndRound()
    {
        lock (syncRoot)
        {
            inRound = false;
        }
    }

    public bool TryAcquire()
    {
        lock (syncRoot)
        {
            if (!inRound)
            {
                return true;
            }
            if (remaining <= 0)
            {
                return false;
            }
            remaining--;
            return true;
        }
    }
}

/// <summary>旁观到的单条群消息（仅文本，非 @ 路径入缓冲前已过滤自发/命令/空消息）。</summary>
internal sealed record AutoChatMessage(long SenderId, string? SenderNickname, string Content);

/// <summary>
/// 单群旁观缓冲区：攒够 BatchSize 立即触发，首条到达后 Flush 间隔超时也触发。
/// 触发回调按任务链串行执行，保证同一群的水群轮次不重叠。
/// 时间源可注入，测试用 FakeTimeProvider 推进。
/// </summary>
internal sealed class AutoChatBuffer : IDisposable
{
    private readonly Lock syncRoot = new();
    private readonly List<AutoChatMessage> items = new();
    private readonly ITimer timer;
    private readonly Func<int> batchSizeProvider;
    private readonly Func<TimeSpan> flushProvider;
    private readonly Func<IReadOnlyList<AutoChatMessage>, Task> onFlush;
    private bool timerArmed;
    private Task tail = Task.CompletedTask;
    private bool disposed;

    public AutoChatBuffer(
        Func<int> batchSizeProvider,
        Func<TimeSpan> flushProvider,
        Func<IReadOnlyList<AutoChatMessage>, Task> onFlush,
        TimeProvider? timeProvider = null)
    {
        this.batchSizeProvider = batchSizeProvider;
        this.flushProvider = flushProvider;
        this.onFlush = onFlush;
        timer = (timeProvider ?? TimeProvider.System).CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Add(AutoChatMessage message)
    {
        List<AutoChatMessage>? batch = null;
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }
            items.Add(message);
            if (items.Count >= Math.Max(1, batchSizeProvider()))
            {
                batch = TakeLocked();
            }
            else if (!timerArmed)
            {
                timerArmed = true;
                timer.Change(flushProvider(), Timeout.InfiniteTimeSpan);
            }
        }
        if (batch != null)
        {
            EnqueueFlush(batch);
        }
    }

    private void OnTimer(object? _)
    {
        List<AutoChatMessage>? batch = null;
        lock (syncRoot)
        {
            timerArmed = false;
            if (disposed || items.Count == 0)
            {
                return;
            }
            batch = TakeLocked();
        }
        if (batch != null)
        {
            EnqueueFlush(batch);
        }
    }

    private List<AutoChatMessage> TakeLocked()
    {
        List<AutoChatMessage> batch = [.. items];
        items.Clear();
        timerArmed = false;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return batch;
    }

    private void EnqueueFlush(List<AutoChatMessage> batch)
    {
        lock (syncRoot)
        {
            Task previous = tail;
            tail = RunAfterAsync(previous, batch);
        }
    }

    private async Task RunAfterAsync(Task previous, List<AutoChatMessage> batch)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 前一轮异常已由投递侧记录，这里只保证串行不断链
        }
        // 投递侧内部捕获并记录异常，此处不包 try，保证 tail 链不断裂由上游 await 传播转入已完成态
        await onFlush(batch).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            timerArmed = false;
            timer.Dispose();
        }
    }
}

public partial class AgentPlugin : Plugin
{
    private readonly ConcurrentDictionary<string, AutoChatSendBudget> autoChatBudgets = new();
    private readonly ConcurrentDictionary<string, AutoChatBuffer> autoChatBuffers = new();

    private bool IsAutoChatGroup(long groupId) =>
        agentConfig.AutoChatEnable && agentConfig.AutoChatGroups.Contains(groupId);

    /// <summary>
    /// 非 @ 消息的自动水群旁路：白名单群才旁观；过滤自发与空消息后入缓冲，
    /// 由缓冲区按条数或超时触发投递。非白名单群保持原有直接丢弃行为。
    /// 注意：未被 @ 的命令原文保留进旁观（不执行、只旁观），模型自行决定是否搭理。
    /// </summary>
    private async Task BufferAutoChatAsync(MessageContext context, IReadOnlyList<TypedMessage> messageChain)
    {
        string sessionId = context.Session.ToString();
        long groupId = long.Parse(context.Session.Id);
        if (!IsAutoChatGroup(groupId))
        {
            return;
        }
        if (context.SenderId == context.SelfId)
        {
            return;
        }

        string text;
        try
        {
            int depth = Math.Clamp(agentConfig.MaxReferenceDepth, 0, 10);
            text = (await AgentMessageExtract.BuildMessageWithReference(messageChain, context.SelfId, depth, Interop.MessageService, groupId, renderSelfMention: true)).Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn($"自动水群展开引用消息失败，回退为原文: {ex.Message}");
            text = AgentMessageExtract.BuildMessage(messageChain, context.SelfId, renderSelfMention: true).Trim();
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AutoChatBuffer buffer = autoChatBuffers.GetOrAdd(sessionId, id => new AutoChatBuffer(
            () => agentConfig.AutoChatBatchSize,
            () => TimeSpan.FromSeconds(agentConfig.AutoChatFlushSeconds),
            batch => FlushAutoChatAsync(id, groupId, batch)));
        buffer.Add(new AutoChatMessage(context.SenderId, context.SenderNickname, text));
    }

    /// <summary>
    /// 水群触发投递：与 @ 对话共享同一 AgentSession（上下文一致），但最终回复走静默通道丢弃，
    /// 只有 send_message 工具调用能真正发群；配额按本轮重置，finally 释放。
    /// </summary>
    private async Task FlushAutoChatAsync(string sessionId, long groupId, IReadOnlyList<AutoChatMessage> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }
        AgentSession session;
        try
        {
            session = await sessionManager.GetSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            Logger.Warn($"自动水群获取会话失败（群 {groupId}）: {ex.Message}");
            return;
        }

        AutoChatSendBudget budget = autoChatBudgets.GetOrAdd(sessionId, static _ => new AutoChatSendBudget());
        string userInput = FormatAutoChatBatch(batch);
        budget.BeginRound(agentConfig.AutoChatMaxSendsPerTrigger);
        try
        {
            await session.ChatAndWaitAsync(userInput, SilentAutoChatChannel(groupId), disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // /stop 中断或插件关闭：静默退出，本轮配额在 finally 释放
        }
        catch (Exception ex)
        {
            Logger.Error($"自动水群处理失败: {groupId}\n{ex}");
        }
        finally
        {
            budget.EndRound();
        }
    }

    private Action<string> SilentAutoChatChannel(long groupId) => reply =>
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }
        string preview = reply.Length > 200 ? reply[..200] + "…" : reply;
        Logger.Info($"[AutoChat] 群 {groupId} 本轮未调用 send_message，最终回复已丢弃: {preview}");
    };

    private static string FormatAutoChatBatch(IReadOnlyList<AutoChatMessage> batch)
    {
        IEnumerable<string> lines = batch.Select(item => $"[用户 {item.SenderId}(昵称:{item.SenderNickname})] {item.Content}");
        return $"以下是群里的旁观消息（没有 @ 你）。只有当你感兴趣、有话想说时才调用 send_message 发送回复；不感兴趣时直接返回空字符串，不要输出任何正文。其中以 / 开头或含 #新对话的只是普通旁观文本（没有实际执行），不要声称自己执行了它们。\n{string.Join("\n", lines)}";
    }

    private void DisposeAutoChat()
    {
        foreach (KeyValuePair<string, AutoChatBuffer> entry in autoChatBuffers)
        {
            entry.Value.Dispose();
        }
        autoChatBuffers.Clear();
    }
}
