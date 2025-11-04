using CommonLib;
using NapcatClient.Action;
using System;
using System.Net.WebSockets;
using System.Text.Json;
using Websocket.Client;
namespace NapcatClient;

public class BotClient
{
    public WebsocketClient WebSocket { get; set; }
    public ISimpleLogger Logger { internal get; set; }
    public Actions Actions { get; private set; }
    public event GroupMessageCallback? OnGroupMessageReceived;

    // 消息接收监控相关字段
    private DateTime _lastMessageTime = DateTime.Now;
    private readonly Timer _messageMonitorTimer;
    private const int MessageTimeoutSeconds = 15; // 消息超时15秒

    public BotClient(string address, string token, ISimpleLogger logger)
    {
        Uri url = new($"{address}?access_token={token}");

        WebSocket = new(url);
        WebSocket.ErrorReconnectTimeout = TimeSpan.FromSeconds(5);
        WebSocket.LostReconnectTimeout = TimeSpan.FromSeconds(30);
        this.Logger = logger;
        WebSocket.ReconnectTimeout = TimeSpan.FromSeconds(10);// need heartbeats
        WebSocket.ReconnectionHappened.Subscribe(WebSocket_Reconnect);
        WebSocket.DisconnectionHappened.Subscribe(d => {
            _ = WebSocket_Disconnected(d)
            .ContinueWith(result => {
                if (result.Exception != null)
                {
                    logger.Error($"Error:{result.Exception.Message}");
                }
            });
        });
        WebSocket.MessageReceived.Subscribe(msg=>WebSocket_OnMessage(msg.Text));
        
        // 初始化消息监控定时器
        _messageMonitorTimer = new Timer(CheckMessageActivity, null, TimeSpan.FromSeconds(MessageTimeoutSeconds), TimeSpan.FromSeconds(MessageTimeoutSeconds));
        
        WebSocket.Start().Wait();
        this.Actions = new Actions(WebSocket,Logger,this);
        Initialize().Wait();
    }
    
    // 消息活动检测方法
    private void CheckMessageActivity(object? state)
    {
        var timeSinceLastMessage = DateTime.Now - _lastMessageTime;
        if (timeSinceLastMessage.TotalSeconds > MessageTimeoutSeconds)
        {
            Logger.Warn($"超过{MessageTimeoutSeconds}秒未收到任何消息，尝试重新连接");
            try
            {
                // 手动触发重连
                WebSocket.Reconnect();
            }
            catch (Exception ex)
            {
                Logger.Error($"手动重连失败: {ex.Message}");
            }
        }
    }
    
    public long SelfId { get; private set; } = -1;
    public string Nickname { get; private set; } = "unknown";
    public async Task Initialize()
    {
        await Task.Delay(100);
        //get account info
        var result = await Actions.GetAccountInfo();
        SelfId = result.userId;
        Nickname = result.nickname;
    }
    public BotClient(string address, string token) : this(address, token, ConsoleLogger.Instance)
    {

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
                ReceivedGroupMessage receivedGroupMessage = 
                    JsonSerializer.Deserialize<ReceivedGroupMessage>(text)!;
                var groupId = receivedGroupMessage.group_id;
                var rawChain = receivedGroupMessage.message!;
                foreach (var item in rawChain)
                {
                    item.ParseJsonDynamic();
                }
                receivedGroupMessage.message = BotUtils.ConcatAdjacencyText(rawChain);
                OnGroupMessageReceived?.Invoke(groupId, receivedGroupMessage.message, receivedGroupMessage);
            }
        }
    }

    private void WebSocket_OnOpen(object? sender, EventArgs e)
    {
        Logger.Info("websocket open");
        // 连接打开时重置消息时间
        _lastMessageTime = DateTime.Now;
    }
}


