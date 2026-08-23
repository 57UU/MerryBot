using Agent.Session;
using Agent.Tools;
using LlmBackend;
using BrowserService;
using CommonLib;
using NapcatClient;
using NapcatClient.MessageType;
using System.Collections.Concurrent;
using System.Text;

namespace BotPlugin;

[PluginTag("agent", "Agent", "强大的Agent机器人")]
public partial class AgentPlugin : Plugin
{
    private readonly ILlmProviderRegistry llmProvider;
    private readonly AgentSessionManager sessionManager;
    private readonly Browser browser;
    private readonly Task persistenceStartTask;
    private readonly FileSkillManagementService skillService;
    private readonly MemoryManagementService memoryService;
    private readonly ConcurrentDictionary<string, PendingGroupMessages> pendingMessages = new();
    /// <summary>插件生命周期取消源：随 Dispose 取消，贯穿会话调用链（Agent → LLM → 工具）</summary>
    private readonly CancellationTokenSource disposeCts = new();
    /// <summary>群消息调用限速（默认 5 次/20 秒），防止消息刷屏打爆模型 API</summary>
    private readonly RateLimiter rateLimiter = new();

    private sealed class PendingGroupMessages
    {
        public object SyncRoot { get; } = new();
        public List<PendingGroupMessage> Items { get; } = [];
        public bool IsDispatching { get; set; }
    }

    /// <summary>控制命令类型：None 为普通聊天消息，New/Compact 为会话控制命令。</summary>
    private enum ControlKind { None, New, Compact }

    private sealed record PendingGroupMessage(long SenderId, string? SenderNickname, string Content, ControlKind Kind = ControlKind.None, string? Topic = null);
    private readonly AgentConfig agentConfig;

    public AgentPlugin(PluginInterop interop, ILlmProviderRegistry llmProvider, AgentConfig agentConfig, AgentServicePlugin servicePlugin) : base(interop)
    {
        this.llmProvider = llmProvider;
        this.agentConfig = agentConfig;
        Logger.Info("agent plugin start");

        // Skill/记忆服务由 AgentServicePlugin 持有（同一程序集，internal 属性可见）；
        // 依赖注入保证 servicePlugin 先于本插件构造完成
        skillService = servicePlugin.SkillService;
        memoryService = servicePlugin.MemoryService;
        browser = new Browser(new BrowserOptions
        {
            BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN"),
        });
        // 会话空闲淘汰时长由配置控制（小时，支持小数）；非法配置（非正数）回退默认 12 小时，避免会话被立即淘汰
        var idleSessionTimeout = agentConfig.IdleSessionTimeoutHours > 0
            ? TimeSpan.FromHours(agentConfig.IdleSessionTimeoutHours)
            : TimeSpan.FromHours(12);
        sessionManager = new AgentSessionManager(CreateAgent, idleSessionTimeout);
        // 调度器由 core 拥有：插件只注册自己的执行器（把任务内容投给本插件的 AgentSession 执行）
        Interop.ClockService.Executor.Inner = new AgentSessionClockExecutor(sessionManager);
        persistenceStartTask = InitializePersistenceAsync();
    }
    private static string BuildMessage(IReadOnlyList<TypedMessage> messageChain, long selfId)
    {
        StringBuilder sb = new();
        foreach (var message in messageChain)
        {
            var text = message switch
            {
                TextData textData => textData.Text,
                // @Bot 只是唤醒标记，不应作为用户输入再交给模型。
                AtData atData when atData.Qq == selfId.ToString() => string.Empty,
                AtData atData => string.Empty,// atData.Qq == "all" ? "[@全体成员]" : $"[@{atData.Qq}]",
                ReplyData replyData => $"[引用消息 {replyData.Id}]",
                FaceData faceData => $"[表情: {faceData.ToChinese()}]",
                MfaceData mfaceData => $"[商城表情: {mfaceData.Summary ?? mfaceData.EmojiId}]",
                DiceData diceData => $"[骰子: {diceData.Result}点]",
                RpsData rpsData => $"[猜拳: {rpsData.Result switch { "1" => "石头", "2" => "剪刀", _ => "布" }}]",
                PokeData pokeData => "[戳一戳]",
                ImageData imageData => $"[图片: {imageData.Summary ?? imageData.File}]",
                RecordData recordData => "[语音]",
                VideoData videoData => $"[视频: {videoData.File}]",
                FileData fileData => $"[文件: {fileData.File}]",
                JsonData jsonData => $"[卡片消息: {jsonData.Data}]",
                MusicData musicData => $"[音乐: {musicData.Title ?? musicData.Id ?? musicData.Url}]",
                ForwardData forwardData => $"[转发消息 {forwardData.Id}]",
                // 新增消息类型：若实现了有意义的 ToString 则直接采用，否则输出可读占位符
                _ => message.ToString(),
            };
            sb.Append(text);
        }
        return sb.ToString();

    }

    private async Task InitializePersistenceAsync()
    {
        // 定时任务存储的初始化已随调度器移至 core（StartClockAsync）；这里只初始化本插件的记忆库
        await DatabaseContextHistory.EnsureInitializedAsync(Interop.PluginStorage.PluginDatabaseScope);
    }


    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, MessageContext context)
    {
        if (!isMentioned)
        {
            return Task.CompletedTask;
        }

        var sessionId = context.Session.ToString();
        var groupId = long.Parse(context.Session.Id);
        var rawText = BuildMessage(messageChain, context.SelfId).Trim();

        // 控制命令：@机器人 /new、/compact（其余命令保持原行为，不进入 agent）
        if (command != null)
        {
            var kind = command.Name.ToLowerInvariant() switch
            {
                "new" => ControlKind.New,
                "compact" => ControlKind.Compact,
                _ => ControlKind.None,
            };
            if (kind == ControlKind.None)
            {
                return Task.CompletedTask;
            }
            var args = string.Join(' ', command.Args);
            Enqueue(sessionId, groupId, new PendingGroupMessage(context.SenderId, context.SenderNickname, string.Empty, kind,
                Topic: kind == ControlKind.Compact ? args : null));
            // /new 带参数时，参数作为新对话第一条消息（与 #新对话 后接内容语义一致）
            if (kind == ControlKind.New && args.Length > 0)
            {
                Enqueue(sessionId, groupId, new PendingGroupMessage(context.SenderId, context.SenderNickname, args));
            }
            return Task.CompletedTask;
        }

        // 关键字触发：#新对话 → 清空上下文；若关键字后还有内容，作为新对话第一条消息
        if (rawText.Contains("#新对话"))
        {
            Enqueue(sessionId, groupId, new PendingGroupMessage(context.SenderId, context.SenderNickname, string.Empty, ControlKind.New));
            var rest = rawText.Replace("#新对话", string.Empty).Trim();
            if (rest.Length > 0)
            {
                Enqueue(sessionId, groupId, new PendingGroupMessage(context.SenderId, context.SenderNickname, rest));
            }
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Task.CompletedTask;
        }

        Enqueue(sessionId, groupId, new PendingGroupMessage(context.SenderId, context.SenderNickname, rawText));
        return Task.CompletedTask;
    }

    /// <summary>入队消息并确保调度器运行：控制命令与普通消息共用同一队列，保证串行互斥。</summary>
    private void Enqueue(string sessionId, long groupId, PendingGroupMessage message)
    {
        var pending = pendingMessages.GetOrAdd(sessionId, static _ => new PendingGroupMessages());
        var shouldStartDispatcher = false;
        lock (pending.SyncRoot)
        {
            pending.Items.Add(message);
            if (!pending.IsDispatching)
            {
                pending.IsDispatching = true;
                shouldStartDispatcher = true;
            }
        }

        if (shouldStartDispatcher)
        {
            _ = DispatchPendingMessagesAsync(sessionId, groupId, pending);
        }
    }

    private async Task DispatchPendingMessagesAsync(
        string sessionId,
        long groupId,
        PendingGroupMessages pending)
    {
        try
        {
            var session = await sessionManager.GetSessionAsync(sessionId);
            while (true)
            {
                // 限速：超过阈值时等待窗口过期再继续，避免消息刷屏打爆模型 API
                if (rateLimiter.CheckIsLimited(groupId))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(rateLimiter.LimitTime), disposeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                List<PendingGroupMessage> batch;
                lock (pending.SyncRoot)
                {
                    if (pending.Items.Count == 0)
                    {
                        pending.IsDispatching = false;
                        // 会话空闲时移除该群条目，避免长期无消息的群泄漏内存；
                        // 期间到达的新消息会创建新条目独立调度，AgentSession 的互斥队列保证不重复处理
                        if (pendingMessages.TryGetValue(sessionId, out var current) && ReferenceEquals(current, pending))
                        {
                            pendingMessages.TryRemove(sessionId, out _);
                        }
                        return;
                    }

                    batch = [.. pending.Items];
                    pending.Items.Clear();
                }
                rateLimiter.Increase(groupId);

                // 控制命令与对话互斥执行（本循环是会话唯一消费者）：
                // Reset 为本地操作，Compact 调 LLM（已受循环顶部限速保护）
                foreach (var item in batch)
                {
                    switch (item.Kind)
                    {
                        case ControlKind.New:
                            // /new 需重建会话：CreateAgent 可能因配置/技能变化重建工具集；
                            // 先清空持久化历史，再移除并重建，让新会话从空历史 + 新工具开始
                            await session.ResetAsync();
                            session = await sessionManager.RebuildSessionAsync(sessionId);
                            SendGroupReply(groupId, [item.SenderId], "已开启新对话");
                            break;
                        case ControlKind.Compact:
                            await session.CompactAsync(disposeCts.Token, item.Topic);
                            SendGroupReply(groupId, [item.SenderId],
                                string.IsNullOrWhiteSpace(item.Topic) ? "已压缩上下文" : $"已按主题「{item.Topic}」压缩上下文");
                            break;
                    }
                }

                var chatItems = batch.Where(static i => i.Kind == ControlKind.None).ToList();
                if (chatItems.Count == 0)
                {
                    continue; // 本批仅控制命令，无需对话
                }
                var userInput = FormatBatch(chatItems);
                var replyTargets = chatItems
                    .Select(static item => item.SenderId)
                    .Distinct()
                    .ToArray();
                await session.ChatAndWaitAsync(
                    userInput,
                    reply => SendGroupReply(groupId, replyTargets, reply),
                    disposeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 插件关闭：静默退出调度循环，释放调度状态
            lock (pending.SyncRoot)
            {
                pending.IsDispatching = false;
            }
        }
        catch (Exception exception)
        {
            Logger.Error($"Agent 消息处理失败: {groupId}\n{exception}");
            // 断流/重试耗尽等终态失败：给群里一条回执，避免用户完全无感知
            var detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (detail.Length > 100)
            {
                detail = detail[..100] + "…";
            }
            SendGroupReply(groupId, [], $"处理失败：{detail}");
            lock (pending.SyncRoot)
            {
                pending.IsDispatching = false;
            }
        }
    }

    private static string FormatBatch(IReadOnlyList<PendingGroupMessage> batch)
    {
        if (batch.Count == 1)
        {
            // 单条消息也标注发送者，与批量格式一致：模型据此识别"当前用户"身份
            //（记忆写入授权等场景需要 user_id）
            return $"[用户 {batch[0].SenderId}(昵称:{batch[0].SenderNickname})] {batch[0].Content}";
        }

        var messages = batch.Select(item => $"[用户 {item.SenderId}(昵称:{item.SenderNickname})] {item.Content}");
        return $"在处理上一轮请求期间，你收到了以下消息。请按消息顺序综合回复：\n{string.Join("\n", messages)}";
    }

    private void SendGroupReply(long groupId, IReadOnlyList<long> replyTargets, string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }

        var chain = replyTargets
            .Select(target => (TypedMessage)AtData.FromAt(target.ToString()))
            .Append(TextData.FromText($" {reply}"))
            .ToList();
        // Channel 内部已捕获异常并记录日志（含插件 id），不会抛出
        _ = Channel.SendMessage(new SessionKey("qq", "group", groupId.ToString()), chain);
    }

    /// <summary>
    /// Agent 消息审计回调：把每条会话消息（user/assistant/tool）以角色为类型写入 ai_messages。
    /// 只提取文本 part，纯非文本消息（如图片）不落库；消息产生即落库，不受上下文压缩/重置影响。
    /// </summary>
    private async void RecordAiAuditMessageAsync(string sessionId, Message message, TokenUsage usage)
    {
        try
        {
            var text = string.Join('\n', message.content.OfType<MessagePartText>().Select(part => part.text)).Trim();
            if (text.Length == 0)
            {
                return;
            }
            await Interop.MessageService.RecordAiMessageAsync(sessionId, message.role.Value, text, usage);
        }
        catch (Exception exception)
        {
            Logger.Warn($"记录 AI 审计消息失败: {exception.Message}");
        }
    }

    public override void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
        sessionManager.Dispose();
        rateLimiter.Dispose();
        browser.Dispose();
    }
}
