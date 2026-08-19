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

    // ── 以下为新增 DIM 默认实现：现有实现类无需改动即可获得这些能力 ──

    /// <summary>按级别转发到对应基础方法（实现类的级别过滤天然生效）。</summary>
    public void Log(LogLevel level, string message)
    {
        switch (level)
        {
            case LogLevel.Trace: Trace(message); break;
            case LogLevel.Debug: Debug(message); break;
            case LogLevel.Info: Info(message); break;
            case LogLevel.Warn: Warn(message); break;
            case LogLevel.Error: Error(message); break;
            case LogLevel.Fatal: Fatal(message); break;
        }
    }

    /// <summary>异常重载：消息后附加异常完整信息（含堆栈）。</summary>
    public void Trace(Exception exception, string message) => Trace(Format(message, exception));
    public void Debug(Exception exception, string message) => Debug(Format(message, exception));
    public void Info(Exception exception, string message) => Info(Format(message, exception));
    public void Warn(Exception exception, string message) => Warn(Format(message, exception));
    public void Error(Exception exception, string message) => Error(Format(message, exception));
    public void Fatal(Exception exception, string message) => Fatal(Format(message, exception));

    /// <summary>格式化重载：按 string.Format 组合消息；格式非法时降级为拼接，日志调用本身绝不抛异常。</summary>
    public void Trace(string format, params object?[] args) => Trace(SafeFormat(format, args));
    public void Debug(string format, params object?[] args) => Debug(SafeFormat(format, args));
    public void Info(string format, params object?[] args) => Info(SafeFormat(format, args));
    public void Warn(string format, params object?[] args) => Warn(SafeFormat(format, args));
    public void Error(string format, params object?[] args) => Error(SafeFormat(format, args));
    public void Fatal(string format, params object?[] args) => Fatal(SafeFormat(format, args));

    private static string Format(string message, Exception exception) =>
        string.IsNullOrEmpty(message) ? exception.ToString() : $"{message} | {exception}";

    private static string SafeFormat(string format, object?[] args)
    {
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            return format + " " + string.Join(", ", args);
        }
    }
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
