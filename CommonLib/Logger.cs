namespace CommonLib;

/// <summary>日志级别，数值越大越严重。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

public interface ISimpleLogger
{
    public void Trace(string message);
    public void Debug(string message);
    public void Info(string message);
    public void Warn(string message);
    public void Error(string message);
    public void Fatal(string message);
}
public class ConsoleLogger : ISimpleLogger
{
    private readonly object _sync = new();
    private ConsoleLogger() { }
    public static ConsoleLogger Instance { get; } = new ConsoleLogger();

    /// <summary>低于该级别的日志将被丢弃（默认输出全部级别）。</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    private void Write(LogLevel level, string tag, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }
        // 加锁避免多线程并发写入时输出交错
        lock (_sync)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {tag}:{message}");
        }
    }

    public void Trace(string message) => Write(LogLevel.Trace, "Trace", message);
    public void Debug(string message) => Write(LogLevel.Debug, "Debug", message);
    public void Info(string message) => Write(LogLevel.Info, "Info", message);
    public void Warn(string message) => Write(LogLevel.Warn, "Warn", message);
    public void Error(string message) => Write(LogLevel.Error, "Error", message);
    public void Fatal(string message) => Write(LogLevel.Fatal, "Fatal", message);
}
