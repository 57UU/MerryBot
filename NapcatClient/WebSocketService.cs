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
    /// <summary>串行化看门狗检查与 Start/Stop/Dispose，避免并发重叠操作 client。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timeLock = new();
    private DateTime _lastMessageTime = DateTime.Now;
    /// <summary>看门狗仅做监控；连接自愈由库内建重连（ErrorReconnectTimeout/LostReconnectTimeout）负责。</summary>
    private const int MessageTimeoutSeconds = 25;
    private static readonly TimeSpan MonitorPeriod = TimeSpan.FromSeconds(15);

    public WebSocketService(string address, string token, ISimpleLogger logger)
    {
        _uri = new($"{address}?access_token={token}");
        Logger = logger;

        WebSocket = new WebsocketClient(_uri);
        SetupWebSocketClient(WebSocket);

        _messageMonitorTimer = new Timer((o) => _ = CheckMessageActivity(), null,
            MonitorPeriod, MonitorPeriod);
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
        _gate.Wait();
        try
        {
            WebSocket.Start().Wait();
            SetLastMessageTime(DateTime.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Stop(WebSocketCloseStatus status, string description)
    {
        await _gate.WaitAsync();
        try
        {
            await WebSocket.Stop(status, description);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnDisconnectedInternal(DisconnectionInfo d)
    {
        Logger.Warn($"websocket disconnect: {d.Type}, {d.CloseStatus}, {d.CloseStatusDescription}");
        SetLastMessageTime(DateTime.Now);
        OnDisconnected?.Invoke(d);
    }

    public void ResetMessageTime()
    {
        SetLastMessageTime(DateTime.Now);
    }

    private void SetLastMessageTime(DateTime time)
    {
        lock (_timeLock)
        {
            _lastMessageTime = time;
        }
    }

    private DateTime GetLastMessageTime()
    {
        lock (_timeLock)
        {
            return _lastMessageTime;
        }
    }

    private async Task CheckMessageActivity()
    {
        // 非阻塞获取信号量：上一次检查未完成时跳过本次，避免并发重叠
        if (!await _gate.WaitAsync(0))
        {
            return;
        }
        try
        {
            var timeSinceLastMessage = DateTime.Now - GetLastMessageTime();
            if (timeSinceLastMessage.TotalSeconds > MessageTimeoutSeconds)
            {
                // 仅记录日志。client 的自愈由库内建重连机制负责（ErrorReconnectTimeout/LostReconnectTimeout），
                // 手动 Stop/Dispose 重建 client 会与库内重连循环冲突，可能造成双重释放。
                Logger.Warn($"超过{MessageTimeoutSeconds}秒未收到任何消息，等待库内建重连机制自愈");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _messageMonitorTimer?.Dispose();
            WebSocket.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}