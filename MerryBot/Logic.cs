using BotPlugin;
using CommonLib;
using DataProvider;
using NapcatClient;
using NapcatClient.MessageType;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace MerryBot;

internal partial class Logic
{
    readonly BotClient botClient;
    private readonly DataProvider.PluginStorageDatabase PluginStorageDatabase;
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public static long AuthorizedUser { get { return ConfigManager.Instance.AuthorizedUser; } }
    readonly string[] CommandLineArguments = Environment.GetCommandLineArgs();
    private readonly EventRegister EventRegister = new();

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
    private (bool isMentioned, string textMessage) ExtractMessage(ReceivedGroupMessage data)
    {
        var chain = data.message;
        var selfId = data.self_id;
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
        ReadOnlySpan<TypedMessage> span = CollectionsMarshal.AsSpan(chain);
        long selfId = BotUtils.GetSelfId(data);
        logger.Info($"on message:{groupId}|{BotUtils.MessageChainToString(span)}");

        long senderId = data.sender.user_id;

        EventRegister.RaiseRawGroupMessageReceived(data);

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

        var (isTargeted, textMessage) = ExtractMessage(data);
        var command = ParseCommand(textMessage);
        OnGroupMessage(isTargeted, command, data);
    }

}
