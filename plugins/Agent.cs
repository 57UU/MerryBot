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
public partial class AgentPlugin : Plugin, ISkillManagementService, IMemoryManagementService
{
    private readonly ILlmProviderRegistry llmProvider;
    private readonly AgentSessionManager sessionManager;
    private readonly Browser browser;
    private readonly PluginClockStore clockStore;
    private readonly ClockService clockService;
    private readonly Task clockServiceStartTask;
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

    private sealed record PendingGroupMessage(long SenderId, string Content);
    private readonly AgentConfig agentConfig;

    public AgentPlugin(PluginInterop interop, ILlmProviderRegistry llmProvider, AgentConfig agentConfig) : base(interop)
    {
        this.llmProvider = llmProvider;
        this.agentConfig = agentConfig;
        Logger.Info("agent plugin start");

        skillService = new FileSkillManagementService(Path.Combine(Interop.PathPrefix, "skills"));
        memoryService = new MemoryManagementService(Interop.PluginStorage.PluginDatabaseScope);
        browser = new Browser(new BrowserOptions
        {
            BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN"),
        });
        sessionManager = new AgentSessionManager(CreateAgent);
        clockStore = new PluginClockStore(Interop.PluginStorage.PluginDatabaseScope);
        clockService = new ClockService(
            clockStore,
            new AgentSessionClockExecutor(sessionManager, RecordCronAiMessageAsync));
        clockServiceStartTask = InitializePersistenceAndClockAsync();
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
                AtData atData => atData.Qq == "all" ? "[@全体成员]" : $"[@{atData.Qq}]",
                ReplyData replyData => $"[回复消息 {replyData.Id}]",
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

    private async Task InitializePersistenceAndClockAsync()
    {
        await clockStore.EnsureInitializedAsync();
        await DatabaseContextHistory.EnsureInitializedAsync(Interop.PluginStorage.PluginDatabaseScope);
        await clockService.StartAsync();
    }


    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, MessageContext context)
    {
        if (!isMentioned || command != null)
        {
            return Task.CompletedTask;
        }

        var userInput = BuildMessage(messageChain, context.SelfId).Trim();
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return Task.CompletedTask;
        }

        var sessionId = context.Session.ToString();
        var pending = pendingMessages.GetOrAdd(sessionId, static _ => new PendingGroupMessages());
        var shouldStartDispatcher = false;
        lock (pending.SyncRoot)
        {
            pending.Items.Add(new PendingGroupMessage(context.SenderId, userInput));
            if (!pending.IsDispatching)
            {
                pending.IsDispatching = true;
                shouldStartDispatcher = true;
            }
        }

        if (shouldStartDispatcher)
        {
            _ = DispatchPendingMessagesAsync(sessionId, long.Parse(context.Session.Id), pending);
        }
        return Task.CompletedTask;
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

                var userInput = FormatBatch(batch);
                var replyTargets = batch
                    .Select(static item => item.SenderId)
                    .Distinct()
                    .ToArray();
                var (reply, usage) = await session.ChatAndWaitAsync(
                    userInput,
                    reply => SendGroupReply(groupId, replyTargets, reply),
                    disposeCts.Token);
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    try
                    {
                        await Interop.MessageService.RecordAiMessageAsync(sessionId, reply, usage);
                    }
                    catch (Exception exception)
                    {
                        Logger.Warn($"记录 AI 消息失败: {groupId}\n{exception.Message}");
                    }
                }
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
            return $"[用户 {batch[0].SenderId}] {batch[0].Content}";
        }

        var messages = batch.Select(item => $"[用户 {item.SenderId}] {item.Content}");
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
            .Append(TextData.FromText(reply))
            .ToList();
        // Channel 内部已捕获异常并记录日志（含插件 id），不会抛出
        _ = Channel.SendMessage(new SessionKey("qq", "group", groupId.ToString()), chain);
    }

    /// <summary>定时任务回复后记录 AI 消息（sessionId 即会话标识，仅文本 + token 用量）。</summary>
    private Task RecordCronAiMessageAsync(string sessionId, string content, TokenUsage usage)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.CompletedTask;
        }
        return Interop.MessageService.RecordAiMessageAsync(sessionId, content, usage);
    }

    public override void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
        rateLimiter.Dispose();
        try
        {
            clockService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            browser.Dispose();
        }
    }

    Task<IReadOnlyList<ManagedSkill>> ISkillManagementService.ListSkillsAsync(CancellationToken cancellationToken)
        => skillService.ListSkillsAsync(cancellationToken);
    Task<string> ISkillManagementService.ReadSkillAsync(string name, bool includeDisabled, CancellationToken cancellationToken)
        => skillService.ReadSkillAsync(name, includeDisabled, cancellationToken);
    Task ISkillManagementService.UploadSkillAsync(SkillUpload upload, CancellationToken cancellationToken)
        => skillService.UploadSkillAsync(upload, cancellationToken);
    Task ISkillManagementService.SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
        => skillService.SetSkillEnabledAsync(name, enabled, cancellationToken);
    Task ISkillManagementService.DeleteSkillAsync(string name, CancellationToken cancellationToken)
        => skillService.DeleteSkillAsync(name, cancellationToken);

    Task<IReadOnlyList<ManagedMemorySession>> IMemoryManagementService.ListMemorySessionsAsync(CancellationToken cancellationToken)
        => memoryService.ListMemorySessionsAsync(cancellationToken);
    Task<string> IMemoryManagementService.GetMemoryIndexAsync(string sessionKey, CancellationToken cancellationToken)
        => memoryService.GetMemoryIndexAsync(sessionKey, cancellationToken);
    Task IMemoryManagementService.SaveMemoryIndexAsync(string sessionKey, string content, CancellationToken cancellationToken)
        => memoryService.SaveMemoryIndexAsync(sessionKey, content, cancellationToken);
    Task<IReadOnlyList<ManagedMemory>> IMemoryManagementService.ListMemoriesAsync(string sessionKey, CancellationToken cancellationToken)
        => memoryService.ListMemoriesAsync(sessionKey, cancellationToken);
    Task<ManagedMemory?> IMemoryManagementService.GetMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken)
        => memoryService.GetMemoryAsync(sessionKey, key, cancellationToken);
    Task IMemoryManagementService.SaveMemoryAsync(string sessionKey, string key, string content, CancellationToken cancellationToken)
        => memoryService.SaveMemoryAsync(sessionKey, key, content, cancellationToken);
    Task<bool> IMemoryManagementService.DeleteMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken)
        => memoryService.DeleteMemoryAsync(sessionKey, key, cancellationToken);
    Task<string?> IMemoryManagementService.GetPromptInjectionAsync(string sessionKey, CancellationToken cancellationToken)
        => memoryService.GetPromptInjectionAsync(sessionKey, cancellationToken);
}
