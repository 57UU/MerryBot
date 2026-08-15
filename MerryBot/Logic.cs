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
        StatusApiMapper.Map(webUiApplication, () => new BotStatusDto(
            botClient.WebSocketService.WebSocket.IsRunning,
            botClient.SelfId >= 0 ? botClient.SelfId.ToString() : "-",
            string.IsNullOrEmpty(botClient.Nickname) ? "-" : botClient.Nickname,
            ConfigManager.Instance.NapcatServer), historyRecorder);
        GroupApiMapper.Map(webUiApplication, this, historyRecorder);
        LogApiMapper.Map(webUiApplication, Path.Combine(botClient.PathPrefix, "log"));
        configRegistry.RegisterConfig("core", ConfigManager.Instance, ConfigManager.Save);
        LoadPlugins();
        _ = RunWebUiAsync();
        botClient.OnGroupMessageReceived += OnGroupMessageReceived;
        RegisterEventHandlers();
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
        return (isTargeted, sb.ToString());
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
        OnGroupMessage(isTargeted, command, ingress.MessageChain, data);
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
