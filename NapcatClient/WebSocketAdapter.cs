using CommonLib;
using System.Net.WebSockets;
using Websocket.Client;

namespace NapcatClient;

/// <summary>
/// 基于 Websocket.Client 的消息适配器实现。
/// 仅负责单次连接尝试与状态维护；连接自愈由宿主按配置间隔轮询驱动（库内自动重连已禁用）。
/// </summary>
public class WebSocketAdapter : IDisposable
{
    public AdapterState State { get; private set; } = AdapterState.Disconnected;
    public event Action<string>? OnMessageReceived;

    private readonly Uri? _uri;
    private readonly ISimpleLogger Logger;
    private WebsocketClient? _client;
    /// <summary>串行化 ConnectAsync 与 Dispose，避免并发操作 client。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _messageMonitorTimer;
    private readonly object _timeLock = new();
    private DateTime _lastMessageTime = DateTime.Now;
    private int _disposed;

    /// <summary>看门狗仅做监控；连接自愈由宿主重连循环负责。</summary>
    private const int MessageTimeoutSeconds = 25;
    private static readonly TimeSpan MonitorPeriod = TimeSpan.FromSeconds(15);

    public WebSocketAdapter(string address, string token, ISimpleLogger logger)
    {
        Logger = logger;

        // 地址无效时不能抛异常导致进程崩溃：记录根因并保持未连接状态，
        // 宿主重连循环会按配置间隔持续尝试，配置修正后重启即可恢复。
        if (!Uri.TryCreate($"{address}?access_token={token}", UriKind.Absolute, out var uri))
        {
            Logger.Error($"NapcatServer 配置无效: '{address}'。请修正配置（示例：ws://127.0.0.1:3001）后重启。");
        }
        _uri = uri;

        _messageMonitorTimer = new Timer(o => _ = CheckMessageActivity(), null,
            MonitorPeriod, MonitorPeriod);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State == AdapterState.Connected || State == AdapterState.Connecting)
            {
                return;
            }
            // 地址无效：保持未连接，等待配置修正后重启
            if (_uri == null)
            {
                return;
            }
            State = AdapterState.Connecting;
            // 每次尝试新建 client（旧 client 可能已处于失败态），并禁用库内自动重连，重连节奏由宿主控制
            _client?.Dispose();
            var client = new WebsocketClient(_uri)
            {
                ReconnectTimeout = null,
                ErrorReconnectTimeout = null,
                LostReconnectTimeout = null,
            };
            _client = client;
            client.ReconnectionHappened.Subscribe(OnReconnection);
            client.DisconnectionHappened.Subscribe(OnDisconnection);
            client.MessageReceived.Subscribe(m =>
            {
                if (m.Text != null)
                {
                    SetLastMessageTime(DateTime.Now);
                    OnMessageReceived?.Invoke(m.Text);
                }
            });
            await client.Start();
        }
        catch (Exception ex)
        {
            Logger.Error($"连接失败: {ex.Message}");
            State = AdapterState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SendAsync(string payload)
    {
        if (State != AdapterState.Connected || _client == null)
        {
            throw new InvalidOperationException("消息适配器未连接，无法发送消息");
        }
        // Websocket.Client 的 Send(string) 为同步阻塞调用（底层同步写流），
        // 保留 Task.Run 以避免阻塞调用线程
        return Task.Run(() => _client.Send(payload));
    }

    private void OnReconnection(ReconnectionInfo info)
    {
        if (info.Type == ReconnectionType.Initial)
        {
            Logger.Info("消息适配器已连接");
            State = AdapterState.Connected;
        }
        else
        {
            State = AdapterState.Disconnected;
        }
        SetLastMessageTime(DateTime.Now);
    }

    private void OnDisconnection(DisconnectionInfo d)
    {
        State = AdapterState.Disconnected;
        SetLastMessageTime(DateTime.Now);
        if (Volatile.Read(ref _disposed) == 0)
        {
            Logger.Warn($"websocket disconnect: {d.Type}, {d.CloseStatus}, {d.CloseStatusDescription}");
        }
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
                // 仅记录日志。连接自愈由宿主重连循环负责，这里不干预 client。
                Logger.Warn($"超过{MessageTimeoutSeconds}秒未收到任何消息，等待宿主重连循环处理");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _gate.Wait();
        try
        {
            _messageMonitorTimer?.Dispose();
            _client?.Dispose();
            _client = null;
            State = AdapterState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }
}
