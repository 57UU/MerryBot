namespace CommonLib;

/// <summary>
/// 全局日志门面。宿主组合根（MerryBot/Entry.cs）在 NLog 配置完成后把
/// <see cref="Default"/> 替换为 NLog 桥接实现，使所有未显式注入 logger 的
/// 调用点（旁路通道）自动汇入统一日志出口。
/// 默认值为 <see cref="ConsoleLogger.Instance"/>，保证独立运行/测试场景可用。
/// </summary>
public static class SimpleLog
{
    /// <summary>当前全局日志实现。仅允许在进程启动阶段（单线程）替换。</summary>
    public static ISimpleLogger Default { get; set; } = ConsoleLogger.Instance;
}
