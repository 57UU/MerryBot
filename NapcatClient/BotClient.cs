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

    public BotClient(string address, string token, ISimpleLogger logger)
    {
        Uri url = new($"{address}?access_token={token}");

        WebSocket = new(url);
        WebSocket.ErrorReconnectTimeout = TimeSpan.FromSeconds(5);
        this.Logger = logger;
        WebSocket.ReconnectTimeout = TimeSpan.FromHours(6);
        WebSocket.ReconnectionHappened.Subscribe(WebSocket_Reconnect);
        WebSocket.DisconnectionHappened.Subscribe(d=>_=WebSocket_Disconnected(d));
        WebSocket.MessageReceived.Subscribe(msg=>WebSocket_OnMessage(msg.Text));
        WebSocket.Start().Wait();
        this.Actions = new Actions(WebSocket,Logger,this);
        Initialize().Wait();
    }
    public long SelfId { get; private set; } = -1;
    public string Nickname { get; private set; } = "unknown";
    bool IsClosed = false;
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
        WebSocket.Dispose();
    }
    private async Task WebSocket_Disconnected(DisconnectionInfo d)
    {
        Logger.Warn($"websocket disconnect:{d.CloseStatusDescription}");
    }
    private void WebSocket_Reconnect(ReconnectionInfo reconnectionInfo)
    {
        Logger.Warn($"websocket reconnect:{reconnectionInfo.Type}");
    }

    private void WebSocket_OnMessage(string? text)
    {
        if (text == null)
        {
            Logger.Trace("empty message received");
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
    }
}


