using CommonLib;
using NapcatClient.Action;
using NapcatClient.EventType;
using NapcatClient.MessageType;
using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Collections.Generic;
using Websocket.Client;
namespace NapcatClient;


public class BotClient
{
    public WebsocketClient WebSocket { get; set; }
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

    // 消息接收监控相关字段
    private DateTime _lastMessageTime = DateTime.Now;
    private readonly Timer _messageMonitorTimer;
    private const int MessageTimeoutSeconds = 15; // 消息超时15秒

    private readonly Uri _uri;
    public BotClient(string address, string token, ISimpleLogger logger, string pathPrefix)
    {
        _uri = new($"{address}?access_token={token}");
        this.Logger = logger;
        PathPrefix = pathPrefix;

        WebSocket = new(_uri);
        SetupWebSocketClient(WebSocket);
        
        // 初始化消息监控定时器
        _messageMonitorTimer = new Timer((o) =>_=CheckMessageActivity(), null, TimeSpan.FromSeconds(MessageTimeoutSeconds), TimeSpan.FromSeconds(MessageTimeoutSeconds));
        
        WebSocket.Start().Wait();
        this.Actions = new Actions(Logger,this);
        _lastMessageTime = DateTime.Now; // 初始化连接状态
        Initialize().Wait();
    }
    
    private void SetupWebSocketClient(WebsocketClient webSocket)
    {
        webSocket.ErrorReconnectTimeout = TimeSpan.FromSeconds(5);
        webSocket.LostReconnectTimeout = TimeSpan.FromSeconds(30);
        webSocket.ReconnectTimeout = TimeSpan.FromSeconds(10);
        webSocket.ReconnectionHappened.Subscribe(WebSocket_Reconnect);
        webSocket.DisconnectionHappened.Subscribe(d => {
            _ = WebSocket_Disconnected(d)
            .ContinueWith(result => {
                if (result.Exception != null)
                {
                    Logger.Error($"Error:{result.Exception.Message}");
                }
            });
        });
        webSocket.MessageReceived.Subscribe(msg=>WebSocket_OnMessage(msg.Text));
    }
    
    // 消息活动检测方法
    private async Task CheckMessageActivity()
    {
        var timeSinceLastMessage = DateTime.Now - _lastMessageTime;
        if (timeSinceLastMessage.TotalSeconds > MessageTimeoutSeconds)
        {
            Logger.Warn($"超过{MessageTimeoutSeconds}秒未收到任何消息，尝试重新连接");
            try
            {
                // 彻底关闭连接
                await WebSocket.Stop(WebSocketCloseStatus.NormalClosure,"no message receivied");
                WebSocket.Dispose();
                
                // 重新创建 WebSocket 实例
                WebSocket = new WebsocketClient(_uri);
                SetupWebSocketClient(WebSocket);
                
                // 重新启动连接
                await WebSocket.Start();
            }
            catch (Exception ex)
            {
                Logger.Error($"手动重连失败: {ex.Message}");
            }
        }
    }
    
    public long SelfId { get; private set; } = -1;
    public string Nickname { get; private set; } = "unknown";
    public string PathPrefix { get; private set; } = "data";
    public async Task Initialize()
    {
        await Task.Delay(100);
        //get account info
        var result = await Actions.GetAccountInfo();
        SelfId = result.userId;
        Nickname = result.nickname;
    }
    public void Close()
    {
        _messageMonitorTimer?.Dispose();
        WebSocket.Dispose();
    }
    private async Task WebSocket_Disconnected(DisconnectionInfo d)
    {
        Logger.Warn($"websocket disconnect:{d.Type},{d.CloseStatus},{d.CloseStatusDescription}");
        // 重置消息时间，避免重连后立即触发消息超时
        _lastMessageTime = DateTime.Now;
    }
    private void WebSocket_Reconnect(ReconnectionInfo reconnectionInfo)
    {
        Logger.Warn($"websocket reconnect:{reconnectionInfo.Type}");
        // 重连后重置消息时间
        _lastMessageTime = DateTime.Now;
    }

    private void WebSocket_OnMessage(string? text)
    {
        // 更新最后消息时间 - 任何消息都表示连接活跃
        _lastMessageTime = DateTime.Now;
        
        if (text == null)
        {
            Logger.Debug("empty message received");
            return;
        }
        Logger.Trace($"websocket on message: {text}");
        var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
        if (message.TryGetValue("echo", out JsonElement echo))
        {
            //return message
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

    private void WebSocket_OnOpen(object? sender, EventArgs e)
    {
        Logger.Info("websocket open");
        // 连接打开时重置消息时间
        _lastMessageTime = DateTime.Now;
    }
}


