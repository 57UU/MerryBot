namespace NapcatClient;

/// <summary>消息适配器连接状态。</summary>
public enum AdapterState
{
    /// <summary>未连接（初始状态或连接已断开）。</summary>
    Disconnected,
    /// <summary>正在尝试建立连接。</summary>
    Connecting,
    /// <summary>已连接，可收发消息。</summary>
    Connected,
}

/// <summary>
/// 消息适配器状态契约：仅暴露当前连接状态，供宿主（core）查询。
/// 连接/收发的具体能力由适配器实现类提供，不进入该接口。
/// </summary>
public interface IAdapterState
{
    /// <summary>当前连接状态。</summary>
    AdapterState State { get; }
}
