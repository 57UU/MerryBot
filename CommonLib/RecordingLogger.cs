namespace CommonLib;

/// <summary>
/// 记录所有日志调用的 ISimpleLogger 测试替身。
/// DIM 新增方法（Log/异常重载/格式化重载）自动落到 6 个基础方法被记录。
/// </summary>
public sealed class RecordingLogger : ISimpleLogger
{
    private readonly object _sync = new();
    private readonly List<(LogLevel Level, string Message)> _records = new();

    public static RecordingLogger Instance { get; } = new RecordingLogger();

    public void Trace(string message) => Record(LogLevel.Trace, message);
    public void Debug(string message) => Record(LogLevel.Debug, message);
    public void Info(string message) => Record(LogLevel.Info, message);
    public void Warn(string message) => Record(LogLevel.Warn, message);
    public void Error(string message) => Record(LogLevel.Error, message);
    public void Fatal(string message) => Record(LogLevel.Fatal, message);

    private void Record(LogLevel level, string message)
    {
        lock (_sync)
        {
            _records.Add((level, message));
        }
    }

    /// <summary>返回当前全部记录的快照（最新在后）。</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
    {
        lock (_sync)
        {
            return _records.ToList();
        }
    }

    /// <summary>清空已记录内容。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
        }
    }
}
