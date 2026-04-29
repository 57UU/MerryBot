using CommonLib;
using NapcatClient.Action;
using NapcatClient.EventType;
using System.Text.Json;
namespace NapcatClient;


public class BotClient
{
    public WebSocketService WebSocketService { get; }
    public ISimpleLogger Logger { internal get; set; }
    public Actions Actions { get; private set; }
    public event GroupMessageCallback? OnGroupMessageReceived;
    public event NoticeEventCallback? OnNoticeEventReceived;
    public event GroupUploadEventCallback? OnGroupUploadEventReceived;
    public event GroupAdminEventCallback? OnGroupAdminEventReceived;
    public event GroupDecreaseEventCallback? OnGroupDecreaseEventReceived;
    public event GroupIncreaseEventCallback? OnGroupIncreaseEventReceived;
    public event GroupBanEventCallback? OnGroupBanEventReceived;
    public event FriendAddEventCallback? OnFriendAddEventReceived;
    public event GroupRecallEventCallback? OnGroupRecallEventReceived;
    public event FriendRecallEventCallback? OnFriendRecallEventReceived;
    public event PokeEventCallback? OnPokeEventReceived;
    public event LuckyKingEventCallback? OnLuckyKingEventReceived;
    public event HonorEventCallback? OnHonorEventReceived;
    public event GroupMsgEmojiLikeEventCallback? OnGroupMsgEmojiLikeEventReceived;
    public event EssenceEventCallback? OnEssenceEventReceived;
    public event GroupCardEventCallback? OnGroupCardEventReceived;

    public long SelfId { get; private set; } = -1;
    public string Nickname { get; private set; } = "unknown";
    public string PathPrefix { get; private set; } = "data";
    public BotClient(string address, string token, ISimpleLogger logger, string pathPrefix)
    {
        this.Logger = logger;
        PathPrefix = pathPrefix;

        WebSocketService = new WebSocketService(address, token, logger);
        WebSocketService.OnMessageReceived += WebSocket_OnMessage;
        WebSocketService.OnMessageReceived += _ => WebSocketService.ResetMessageTime();
        WebSocketService.OnDisconnected += _ => WebSocketService.ResetMessageTime();
        WebSocketService.OnReconnected += _ => WebSocketService.ResetMessageTime();

        WebSocketService.Start();
        this.Actions = new Actions(Logger, WebSocketService, () => SelfId);
        Initialize().Wait();
    }

    public async Task Initialize()
    {
        await Task.Delay(100);
        var result = await Actions.GetAccountInfo();
        SelfId = result.userId;
        Nickname = result.nickname;
    }

    public void Close()
    {
        WebSocketService.Dispose();
    }

    private void WebSocket_OnMessage(string? text)
    {
        if (text == null)
        {
            Logger.Debug("empty message received");
            return;
        }
        Logger.Trace($"websocket on message: {text}");
        var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
        if (message.TryGetValue("echo", out JsonElement echo))
        {
            Actions.AddResponse(echo.GetString()!, JsonSerializer.Deserialize<ResponseRootObject>(text)!);
        }
        if (message.TryGetValue("message_type", out JsonElement value))
        {
            var messageType = ((JsonElement)value).GetString();

            if (messageType == "group")
            {
                ReceivedGroupMessage receivedGroupMessage = BotUtils.Deserialize<ReceivedGroupMessage>(text);
                var groupId = receivedGroupMessage.group_id;
                var rawChain = receivedGroupMessage.message!;
                receivedGroupMessage.message = BotUtils.ConcatAdjacencyText(rawChain);
                OnGroupMessageReceived?.Invoke(groupId, receivedGroupMessage.message, receivedGroupMessage);
            }
        }
        else if (message.TryGetValue("post_type", out JsonElement postTypeValue))
        {
            var postType = postTypeValue.GetString();

            if (postType == "notice")
            {
                HandleNoticeEvent(text);
            }
        }
    }

    private void HandleNoticeEvent(string text)
    {
        try
        {
            var noticeEvent = BotUtils.Deserialize<NoticeEvent>(text);
            OnNoticeEventReceived?.Invoke(noticeEvent);

            switch (noticeEvent.NoticeType)
            {
                case "group_upload":
                    var groupUploadEvent = BotUtils.Deserialize<GroupUploadEvent>(text);
                    OnGroupUploadEventReceived?.Invoke(groupUploadEvent);
                    break;
                case "group_admin":
                    var groupAdminEvent = BotUtils.Deserialize<GroupAdminEvent>(text);
                    OnGroupAdminEventReceived?.Invoke(groupAdminEvent);
                    break;
                case "group_decrease":
                    var groupDecreaseEvent = BotUtils.Deserialize<GroupDecreaseEvent>(text);
                    OnGroupDecreaseEventReceived?.Invoke(groupDecreaseEvent);
                    break;
                case "group_increase":
                    var groupIncreaseEvent = BotUtils.Deserialize<GroupIncreaseEvent>(text);
                    OnGroupIncreaseEventReceived?.Invoke(groupIncreaseEvent);
                    break;
                case "group_ban":
                    var groupBanEvent = BotUtils.Deserialize<GroupBanEvent>(text);
                    OnGroupBanEventReceived?.Invoke(groupBanEvent);
                    break;
                case "friend_add":
                    var friendAddEvent = BotUtils.Deserialize<FriendAddEvent>(text);
                    OnFriendAddEventReceived?.Invoke(friendAddEvent);
                    break;
                case "group_recall":
                    var groupRecallEvent = BotUtils.Deserialize<GroupRecallEvent>(text);
                    OnGroupRecallEventReceived?.Invoke(groupRecallEvent);
                    break;
                case "friend_recall":
                    var friendRecallEvent = BotUtils.Deserialize<FriendRecallEvent>(text);
                    OnFriendRecallEventReceived?.Invoke(friendRecallEvent);
                    break;
                case "poke":
                    var pokeEvent = BotUtils.Deserialize<PokeEvent>(text);
                    OnPokeEventReceived?.Invoke(pokeEvent);
                    break;
                case "lucky_king":
                    var luckyKingEvent = BotUtils.Deserialize<LuckyKingEvent>(text);
                    OnLuckyKingEventReceived?.Invoke(luckyKingEvent);
                    break;
                case "honor":
                    var honorEvent = BotUtils.Deserialize<HonorEvent>(text);
                    OnHonorEventReceived?.Invoke(honorEvent);
                    break;
                case "group_msg_emoji_like":
                    var groupMsgEmojiLikeEvent = BotUtils.Deserialize<GroupMsgEmojiLikeEvent>(text);
                    OnGroupMsgEmojiLikeEventReceived?.Invoke(groupMsgEmojiLikeEvent);
                    break;
                case "essence":
                    var essenceEvent = BotUtils.Deserialize<EssenceEvent>(text);
                    OnEssenceEventReceived?.Invoke(essenceEvent);
                    break;
                case "group_card":
                    var groupCardEvent = BotUtils.Deserialize<GroupCardEvent>(text);
                    OnGroupCardEventReceived?.Invoke(groupCardEvent);
                    break;
                default:
                    Logger.Debug($"Unknown notice type: {noticeEvent.NoticeType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error handling notice event: {ex.Message}");
        }
    }
}