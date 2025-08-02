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
    public BotClient(string address, string token) : this(address, token, new ConsoleLogger())
    {

    }

    private void WebSocket_OnClose(object? sender, CloseEventArgs e)
    {
        Logger.Error($"websocket closed: {e.Reason}");
    }

    private void WebSocket_OnError(object? sender, WebSocketSharp.ErrorEventArgs e)
    {
        Logger.Warn($"websocket error: {e.Message}");
    }

    private void WebSocket_OnMessage(object? sender, MessageEventArgs e)
    {
        Logger.Trace($"websocket on message: {e.Data}");
        var message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.Data)!;
        JsonElement echo;
        if (message.TryGetValue("echo", out echo))
        {
            //return message
            Actions.AddResponse(echo.GetString(), message);
        }
        List<Message>? messageChain = null;
        if (message.ContainsKey("message_type"))
        {
            var messageType = ((JsonElement)message["message_type"]).GetString();
            if (message.ContainsKey("message"))
            {
                messageChain = Message.ParseMessageChain(message["message"]);
            }
            if (messageType == "group")
            {
                ReceivedGroupMessage receivedGroupMessage = 
                    JsonSerializer.Deserialize<ReceivedGroupMessage>(e.Data)!;
                var groupId = receivedGroupMessage.group_id;
                receivedGroupMessage.message = messageChain;
                OnGroupMessageReceived?.Invoke(groupId, receivedGroupMessage.message, receivedGroupMessage);
            }
        }
    }

    private void WebSocket_OnOpen(object? sender, EventArgs e)
    {
        Logger.Info("websocket open");
    }
}


