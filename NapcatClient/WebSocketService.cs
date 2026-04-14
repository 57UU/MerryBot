using CommonLib;
using System.Net.WebSockets;
using System.Text.Json;
using Websocket.Client;

namespace NapcatClient;

public class WebSocketService : IDisposable
{
    public WebsocketClient WebSocket { get; private set; }
    public ISimpleLogger Logger { get; }

    public event Action<string>? OnMessageReceived;
    public event Action<ReconnectionInfo>? OnReconnected;
    public event Action<DisconnectionInfo>? OnDisconnected;

    private readonly Uri _uri;
    private readonly Timer _messageMonitorTimer;
    private DateTime _lastMessageTime = DateTime.Now;
    private const int MessageTimeoutSeconds = 15;

    public WebSocketService(string address, string token, ISimpleLogger logger)
    {
        _uri = new($"{address}?access_token={token}");
        Logger = logger;

        WebSocket = new WebsocketClient(_uri);
        SetupWebSocketClient(WebSocket);

        _messageMonitorTimer = new Timer((o) => _ = CheckMessageActivity(), null,
            TimeSpan.FromSeconds(MessageTimeoutSeconds), TimeSpan.FromSeconds(MessageTimeoutSeconds));
    }

    private void SetupWebSocketClient(WebsocketClient webSocket)
    {
        webSocket.ErrorReconnectTimeout = TimeSpan.FromSeconds(5);
        webSocket.LostReconnectTimeout = TimeSpan.FromSeconds(30);
        webSocket.ReconnectTimeout = TimeSpan.FromSeconds(10);
        webSocket.ReconnectionHappened.Subscribe(info => OnReconnected?.Invoke(info));
        webSocket.DisconnectionHappened.Subscribe(d =>
        {
            _ = OnDisconnectedInternal(d).ContinueWith(result =>
            {
                if (result.Exception != null)
                {
                    Logger.Error($"Error: {result.Exception.Message}");
                }
            });
        });
        webSocket.MessageReceived.Subscribe(msg => OnMessageReceived?.Invoke(msg.Text ?? string.Empty));
    }

    public void Start()
    {
        WebSocket.Start().Wait();
        _lastMessageTime = DateTime.Now;
    }

    public async Task Stop(WebSocketCloseStatus status, string description)
    {
        await WebSocket.Stop(status, description);
    }

    private async Task OnDisconnectedInternal(DisconnectionInfo d)
    {
        Logger.Warn($"websocket disconnect: {d.Type}, {d.CloseStatus}, {d.CloseStatusDescription}");
        _lastMessageTime = DateTime.Now;
        OnDisconnected?.Invoke(d);
    }

    public void ResetMessageTime()
    {
        _lastMessageTime = DateTime.Now;
    }

    private async Task CheckMessageActivity()
    {
        var timeSinceLastMessage = DateTime.Now - _lastMessageTime;
        if (timeSinceLastMessage.TotalSeconds > MessageTimeoutSeconds)
        {
            Logger.Warn($"超过{MessageTimeoutSeconds}秒未收到任何消息，尝试重新连接");
            try
            {
                await WebSocket.Stop(WebSocketCloseStatus.NormalClosure, "no message received");
                WebSocket.Dispose();

                WebSocket = new WebsocketClient(_uri);
                SetupWebSocketClient(WebSocket);

                await WebSocket.Start();
            }
            catch (Exception ex)
            {
                Logger.Error($"手动重连失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _messageMonitorTimer?.Dispose();
        WebSocket.Dispose();
    }
}