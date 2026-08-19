namespace CommonLib;

/// <summary>丢弃所有日志的 ISimpleLogger 实现，用于测试与默认降级场景。</summary>
public sealed class NullLogger : ISimpleLogger
{
    public static NullLogger Instance { get; } = new NullLogger();

    private NullLogger() { }

    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Fatal(string message) { }
}
