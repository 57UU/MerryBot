using BotPlugin;
using CommonLib;
using DataProvider;
using NapcatClient;
using NapcatClient.MessageType;
using System.Runtime.InteropServices;

namespace MerryBot;

internal partial class Logic
{
    readonly BotClient botClient;
    private readonly DataProvider.PluginStorageDatabase PluginStorageDatabase;
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public static long AuthorizedUser { get { return ConfigManager.Instance.AuthorizedUser; } }
    readonly string[] CommandLineArguments = Environment.GetCommandLineArgs();

    public Logic(BotClient botClient, string dbPath)
    {
        this.botClient = botClient;
        PluginStorageDatabase = new(dbPath);
        LoadPlugins();
        botClient.OnGroupMessageReceived += OnGroupMessageReceived;
        RegisterEventHandlers();
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
    bool IsTargeted(ReceivedGroupMessage data)
    {
        var chain = data.message;
        var selfId = data.self_id;
        bool isTargeted = false;
        if (chain[0] is AtData atData)
        {
            string target = atData.Qq;
            if (target == selfId.ToString())
            {
                isTargeted = true;
            }
        }
        return isTargeted;
    }
    public void MainPluginInvokeNotInGroup(long groupId, List<TypedMessage> chain, ReceivedGroupMessage data)
    {
        if (mainPlugin == null)
        {
            logger.Error("Main Plugin is not loaded!");
            return;
        }
        if (IsTargeted(data))
        {
            mainPlugin.OnMessageMentionedNotInGroup(groupId, CollectionsMarshal.AsSpan(chain)[1..], data);
        }
    }
    public event Action<ReceivedGroupMessage>? OnRawGroupMessageReceived;

    public void OnGroupMessageReceived(long groupId, List<TypedMessage> chain, ReceivedGroupMessage data)
    {
        if (chain.Count == 0)
        {
            return;
        }
        if (!QqGroupIDs.Contains(groupId))
        {
            MainPluginInvokeNotInGroup(groupId, chain, data);
            return;
        }
        ReadOnlySpan<TypedMessage> span = CollectionsMarshal.AsSpan(chain);
        bool isTargeted = false;
        long selfId = BotUtils.GetSelfId(data);
        logger.Info($"on message:{groupId}|{BotUtils.MessageChainToString(span)}");

        long senderId = data.sender.user_id;

        OnRawGroupMessageReceived?.Invoke(data);

        bool isIntercepted = false;
        foreach (var plugInfo in plugins)
        {
            var interceptors = plugInfo.Interop.Interceptors.ToList();
            foreach (var interceptor in interceptors)
            {
                if (interceptor(data))
                {
                    isIntercepted = true;
                    break;
                }
            }
        }
        if (isIntercepted)
        {
            return;
        }

        isTargeted = IsTargeted(data);

        if (isTargeted)
        {
            // at消息
            OnGroupMessageMentioned(groupId, span[1..], data);
        }
        else
        {
            OnGroupMessageNotMentioned(groupId, span, data);
        }
        OnGroupMessage(groupId, span, data);
    }

}
