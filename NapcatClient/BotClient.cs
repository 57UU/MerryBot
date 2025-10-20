using NapcatClient.Action;
using WebSocketSharp;
using CommonLib;
using System.Text.Json;

namespace NapcatClient;

public class BotClient
{
    public WebSocket WebSocket { get; set; }
    public ISimpleLogger Logger { internal get; set; }
    public Actions Actions { get; private set; }
    public event GroupMessageCallback? OnGroupMessageReceived;

    public BotClient(string address, string token, ISimpleLogger logger)
    {
        WebSocket = new WebSocket($"{address}?access_token={token}");
        this.Logger = logger;
        WebSocket.OnOpen += WebSocket_OnOpen;
        WebSocket.OnMessage += WebSocket_OnMessage;
        WebSocket.OnError += WebSocket_OnError;
        WebSocket.OnClose += WebSocket_OnClose;
        WebSocket.Connect();
        this.Actions = new Actions(WebSocket,Logger,this);
        Initialize().Wait();
    }
    public long SelfId { get; private set; }
    public string Nickname { get; private set; }
    public async Task Initialize()
    {
        await Task.Delay(200);
        //get account info
        var result = await Actions.GetAccountInfo();
        SelfId = result.userId;
        Nickname = result.nickname;
    }
    public BotClient(string address, string token) : this(address, token, ConsoleLogger.Instance)
    {

    }

    private void WebSocket_OnClose(object? sender, CloseEventArgs e)
    {
        Logger.Error($"websocket closed: {e.Reason}");
        Reconnect();
    }

    private void WebSocket_OnError(object? sender, WebSocketSharp.ErrorEventArgs e)
    {
        Logger.Warn($"websocket error: {e.Message}");
        WebSocket.Close();
    }
    private async Task Reconnect()
    {
        const int maxRetry = 3;
        const int retryDelay = 5000; // 5秒

        for (int i = 0; i < maxRetry; i++)
        {
            try
            {
                Logger.Info($"尝试第 {i + 1} 次重连...");
                await Task.Delay(retryDelay);
                WebSocket.Connect();
                Logger.Info("重连成功");
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"第 {i + 1} 次重连失败: {ex.Message}");
            }
        }

        Logger.Error($"已达到最大重连次数({maxRetry})，放弃重连");
    }

    private void WebSocket_OnMessage(object? sender, MessageEventArgs e)
    {
        Logger.Trace($"websocket on message: {e.Data}");
        var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.Data)!;
        if (message.TryGetValue("echo", out JsonElement echo))
        {
            //return message
            Actions.AddResponse(echo.GetString()!, JsonSerializer.Deserialize<ResponseRootObject>(e.Data)!);
        }
        List<Message>? messageChain = null;
        if (message.TryGetValue("message_type", out JsonElement value))
        {
            var messageType = ((JsonElement)value).GetString();
            if (message.TryGetValue("message", out JsonElement messageValue))
            {
                messageChain = Message.ParseMessageChain(messageValue);
            }
            if (messageType == "group")
            {
                ReceivedGroupMessage receivedGroupMessage = 
                    JsonSerializer.Deserialize<ReceivedGroupMessage>(e.Data)!;
                var groupId = receivedGroupMessage.group_id;
                receivedGroupMessage.message = messageChain!;
                OnGroupMessageReceived?.Invoke(groupId, receivedGroupMessage.message, receivedGroupMessage);
            }
        }
    }

    private void WebSocket_OnOpen(object? sender, EventArgs e)
    {
        Logger.Info("websocket open");
    }
}


