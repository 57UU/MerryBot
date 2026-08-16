using Agent.Session;
using BotPlugin;
using CommonLib;
using DataProvider;
using DataService;
using MerryBot.WebUI.Api;
using Microsoft.AspNetCore.Builder;
using NapcatClient;
using NapcatClient.MessageType;
using System.Collections.Immutable;
using System.Text;

namespace MerryBot;

internal partial class Logic
{
    readonly BotClient botClient;
    private readonly DataProvider.PluginStorageDatabase PluginStorageDatabase;
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public static long AuthorizedUser { get { return ConfigManager.Instance.AuthorizedUser; } }
    private readonly EventRegister EventRegister = new();
    private readonly HistoryRecorder historyRecorder;
    private readonly MessageService messageService;
    private readonly WebApplication webUiApplication;
    private readonly ConfigRegistry configRegistry;
    /// <summary>core 拥有的进程生命周期服务（版本/更新/重启/重载/退出），插件与 WebUI 共用</summary>
    private readonly HostLifecycle hostLifecycle;
    /// <summary>core 拥有的定时任务调度器：Agent 插件只注册执行器，生命周期归宿主</summary>
    private readonly ClockService clockService;
    private readonly CoreClockStore clockStore;

    public Logic(BotClient botClient, PluginStorageDatabase pluginStorageDatabase)
    {
        this.botClient = botClient;
        PluginStorageDatabase = pluginStorageDatabase;
        var historyDbPath = Path.Combine(botClient.PathPrefix, "group_history.db");
        var historyStoragePath = Path.Combine(botClient.PathPrefix, "storage");
        historyRecorder = new HistoryRecorder(historyDbPath, historyStoragePath, GetCoreMachineCode());
        historyRecorder.MigrateAsync().GetAwaiter().GetResult();
        messageService = new MessageService(botClient.Bot, historyRecorder, logger, ConfigManager.Instance.ResourceSizeLimitMb * 1024 * 1024);
        webUiApplication = MerryBot.WebUI.Program.CreateApp(historyRecorder, GetCoreWebAddress());
        configRegistry = new ConfigRegistry(webUiApplication.Logger);
        ConfigApiMapper.Map(webUiApplication, configRegistry, Shutdown);
        // hostLifecycle 先于 StatusApiMapper 创建：概览页需展示 git 版本信息
        hostLifecycle = new HostLifecycle(Shutdown, PluginStorageDatabase);
        StatusApiMapper.Map(webUiApplication, () => new BotStatusDto(
            botClient.State == AdapterState.Connected,
            botClient.SelfId?.ToString() ?? "-",
            botClient.Nickname ?? "-",
            ConfigManager.Instance.NapcatServer), historyRecorder, hostLifecycle);
        GroupApiMapper.Map(webUiApplication, this, historyRecorder);
        LogApiMapper.Map(webUiApplication, Path.Combine(botClient.PathPrefix, "log"));
        UpdateApiMapper.Map(webUiApplication, hostLifecycle);
        configRegistry.RegisterConfig("core", ConfigManager.Instance, ConfigManager.Save);
        // 调度器先于插件创建；存储用 core 自己的命名空间（prefix "core"），与插件数据隔离
        clockStore = new CoreClockStore(PluginStorageDatabase.CreateScope("clock", prefix: "core"));
        clockService = new ClockService(clockStore, new DelegatingClockExecutor());
        _ = StartClockAsync();
        LoadPlugins();
        _ = RunWebUiAsync();
        _ = ReconnectLoopAsync();
        botClient.OnGroupMessageReceived += OnGroupMessageReceived;
        RegisterEventHandlers();
    }

    private readonly CancellationTokenSource _reconnectCts = new();
    private bool _reconnectLogged;
    /// <summary>是否曾经成功连接过；用于区分首次连接与真正的断线重连，避免启动时输出假 WARN。</summary>
    private bool _hasEverConnected;

    /// <summary>
    /// 宿主重连循环：适配器未连接时按配置间隔轮询驱动单次连接尝试。
    /// 适配器本身不负责重连（库内自动重连已禁用），重连节奏全部由这里控制。
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        while (!_reconnectCts.IsCancellationRequested)
        {
            if (botClient.State != AdapterState.Connected)
            {
                if (!_reconnectLogged)
                {
                    _reconnectLogged = true;
                    if (_hasEverConnected)
                    {
                        logger.Warn($"消息适配器未连接，每{ConfigManager.Instance.ReconnectIntervalSeconds}秒尝试重连 {ConfigManager.Instance.NapcatServer}");
                    }
                    else
                    {
                        logger.Info($"正在连接消息适配器 {ConfigManager.Instance.NapcatServer}");
                    }
                }
                try
                {
                    await botClient.Adapter.ConnectAsync(_reconnectCts.Token);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "连接尝试失败");
                }
            }
            else
            {
                _reconnectLogged = false;
                _hasEverConnected = true;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, ConfigManager.Instance.ReconnectIntervalSeconds)), _reconnectCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>后台启动定时任务调度器；异常（如存储损坏）记录日志而不是静默丢失。</summary>
    private async Task StartClockAsync()
    {
        try
        {
            await clockStore.EnsureInitializedAsync();
            await clockService.StartAsync();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "ClockService 启动失败");
        }
    }

    /// <summary>后台运行 WebUI；异常（如端口占用）记录日志而不是静默丢失。</summary>
    private async Task RunWebUiAsync()
    {
        try
        {
            await webUiApplication.RunAsync();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "WebUI 启动/运行失败");
        }
    }

    private void RegisterEventHandlers()
    {
        botClient.OnNoticeEventReceived += OnNoticeEventReceived;
        botClient.OnGroupUploadEventReceived += OnGroupUploadEventReceived;
        botClient.OnGroupAdminEventReceived += OnGroupAdminEventReceived;
        botClient.OnGroupDecreaseEventReceived += OnGroupDecreaseEventReceived;
        botClient.OnGroupIncreaseEventReceived += OnGroupIncreaseEventReceived;
        botClient.OnGroupBanEventReceived += OnGroupBanEventReceived;
        botClient.OnFriendAddEventReceived += OnFriendAddEventReceived;
        botClient.OnGroupRecallEventReceived += OnGroupRecallEventReceived;
        botClient.OnFriendRecallEventReceived += OnFriendRecallEventReceived;
        botClient.OnPokeEventReceived += OnPokeEventReceived;
        botClient.OnLuckyKingEventReceived += OnLuckyKingEventReceived;
        botClient.OnHonorEventReceived += OnHonorEventReceived;
        botClient.OnGroupMsgEmojiLikeEventReceived += OnGroupMsgEmojiLikeEventReceived;
        botClient.OnEssenceEventReceived += OnEssenceEventReceived;
        botClient.OnGroupCardEventReceived += OnGroupCardEventReceived;
    }
    private static (bool isMentioned, string textMessage) ExtractMessage(IReadOnlyList<TypedMessage> chain, long selfId)
    {
        bool isTargeted = false;
        var sb = new StringBuilder();
        foreach (var item in chain)
        {
            if (item is TextData textData)
            {
                sb.Append(textData.Text);
            }
            else if (item is AtData atData && atData.Qq == selfId.ToString())
            {
                isTargeted = true;
            }
            else
            {
                sb.Append(item.ToString());
            }
        }
        return (isTargeted, sb.ToString().Trim());
    }
    private Command? ParseCommand(string textMessage)
    {
        if (string.IsNullOrWhiteSpace(textMessage) || textMessage[0] != '/')
        {
            return null;
        }
        textMessage = textMessage[1..];
        var args = textMessage.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string name = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 0 ? args[1..] : [];
        return new Command(name, [.. rest]);

    }
    public void OnGroupMessageReceived(long groupId, List<TypedMessage> chain, ReceivedGroupMessage data)
    {
        if (chain.Count == 0)
        {
            return;
        }
        if (!QqGroupIDs.Contains(groupId))
        {
            return;
        }
        // 群消息日志只保留群号/发送者/消息链长度摘要，避免完整消息链导致日志膨胀与隐私泄露
        logger.Debug($"on message:{groupId}|{data.sender.user_id}|chain:{chain.Count}");
        var ingress = messageService.Ingest(data);

        var (isTargeted, textMessage) = ExtractMessage(ingress.MessageChain, data.self_id);
        var command = ParseCommand(textMessage);
        var context = new MessageContext(new SessionKey("qq", "group", groupId.ToString()), data.sender.user_id, data.sender.nickname, data.self_id);
        OnMessage(isTargeted, command, ingress.MessageChain, context);
        _ = messageService.PrefetchAsync(ingress);
    }

    private static int GetCoreMachineCode()
    {
        var machineCode = ConfigManager.Instance.MachineCode;
        if (machineCode is >= 0 and < 32)
        {
            return machineCode;
        }

        machineCode = Random.Shared.Next(0, 32);
        ConfigManager.Instance.MachineCode = machineCode;
        ConfigManager.Save().GetAwaiter().GetResult();
        return machineCode;
    }

    private static string GetCoreWebAddress()
    {
        return string.IsNullOrWhiteSpace(ConfigManager.Instance.WebAddress)
            ? "http://0.0.0.0:5000"
            : ConfigManager.Instance.WebAddress;
    }

}
