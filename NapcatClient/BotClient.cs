using NapcatClient.Action;
using Newtonsoft.Json;
using WebSocketSharp;
using CommonLib;

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
        this.Actions = new Actions(WebSocket,Logger);
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
        var message = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(e.Data)!;
        dynamic echo;
        if (message.TryGetValue("echo", out echo) && echo is string)
        {
            //return message
            Actions.AddResponse(echo, message);
        }
        List<Message>? messageChain = null;
        if (message.ContainsKey("message_type"))
        {
            var messageType = message["message_type"];
            if (message.ContainsKey("message"))
            {
                messageChain = Message.ParseMessageChain(message["message"]);
            }
            if (messageType == "group")
            {
                ReceivedGroupMessage receivedGroupMessage = 
                    JsonConvert.DeserializeObject<ReceivedGroupMessage>(e.Data)!;
                var groupId = receivedGroupMessage.group_id;
                OnGroupMessageReceived?.Invoke(groupId, receivedGroupMessage.message, receivedGroupMessage);
            }
        }
    }

    private void WebSocket_OnOpen(object? sender, EventArgs e)
    {
        Logger.Info("websocket open");
    }
}


