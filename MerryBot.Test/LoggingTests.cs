using CommonLib;

namespace MerryBot.Test;

/// <summary>
/// ISimpleLogger 增强（DIM 方法）、SimpleLog 门面与测试替身的单元测试。
/// 注意：DIM 方法仅在静态类型为 ISimpleLogger 的变量上可见，因此断言前先经接口调用。
/// </summary>
public sealed class LoggingTests
{
    // ── DIM: Log(level, message) 转发 ──────────────────────────────────────

    [Fact]
    public void Log_Routes_To_Corresponding_Base_Method()
    {
        var recorder = new RecordingLogger();
        ISimpleLogger log = recorder;
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal })
        {
            recorder.Clear();
            log.Log(level, $"msg-{level}");
            var record = Assert.Single(recorder.Snapshot());
            Assert.Equal(level, record.Level);
            Assert.Equal($"msg-{level}", record.Message);
        }
    }

    // ── DIM: 异常重载 ─────────────────────────────────────────────────────

    [Fact]
    public void Exception_Overload_Appends_Exception_Info()
    {
        var recorder = new RecordingLogger();
        ISimpleLogger log = recorder;
        var ex = new InvalidOperationException("boom");
        log.Error(ex, "发生错误");
        var record = Assert.Single(recorder.Snapshot());
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Contains("发生错误", record.Message);
        Assert.Contains("boom", record.Message);
        Assert.Contains("InvalidOperationException", record.Message);
    }

    [Fact]
    public void Exception_Overload_With_Empty_Message_Uses_Exception_Only()
    {
        var recorder = new RecordingLogger();
        ISimpleLogger log = recorder;
        var ex = new InvalidOperationException("boom");
        log.Warn(ex, "");
        var record = Assert.Single(recorder.Snapshot());
        // 空消息时输出 exception.ToString()（含类型名与堆栈）
        Assert.Contains("boom", record.Message);
        Assert.Contains("InvalidOperationException", record.Message);
    }

    // ── DIM: 格式化重载 ───────────────────────────────────────────────────

    [Fact]
    public void Format_Overload_Formats_With_Args()
    {
        var recorder = new RecordingLogger();
        ISimpleLogger log = recorder;
        log.Info("用户 {0} 发送了 {1} 条消息", "张三", 3);
        var record = Assert.Single(recorder.Snapshot());
        Assert.Equal("用户 张三 发送了 3 条消息", record.Message);
    }

    [Fact]
    public void Format_Overload_With_Invalid_Format_Falls_Back_Without_Throwing()
    {
        var recorder = new RecordingLogger();
        ISimpleLogger log = recorder;
        log.Info("非法格式 {0} {1", "a", "b"); // 缺右花括号
        var record = Assert.Single(recorder.Snapshot());
        // 不抛异常，降级为拼接
        Assert.Contains("a", record.Message);
        Assert.Contains("b", record.Message);
    }

    // ── SimpleLog 门面 ────────────────────────────────────────────────────

    [Fact]
    public void SimpleLog_Default_Is_ConsoleLogger_By_Default()
    {
        Assert.Same(ConsoleLogger.Instance, SimpleLog.Default);
    }

    [Fact]
    public void SimpleLog_Default_Can_Be_Replaced_And_Used()
    {
        var original = SimpleLog.Default;
        try
        {
            var recorder = new RecordingLogger();
            SimpleLog.Default = recorder;
            SimpleLog.Default.Warn("通过门面记录");
            var record = Assert.Single(recorder.Snapshot());
            Assert.Equal(LogLevel.Warn, record.Level);
        }
        finally
        {
            SimpleLog.Default = original;
        }
    }

    // ── NullLogger 吞日志 ─────────────────────────────────────────────────

    [Fact]
    public void NullLogger_Swallows_All_Levels()
    {
        ISimpleLogger log = NullLogger.Instance;
        // 不应抛异常
        log.Trace("t");
        log.Debug("d");
        log.Info("i");
        log.Warn("w");
        log.Error(new InvalidOperationException("e"), "err");
        log.Fatal("f");
        log.Log(LogLevel.Error, "x");
        Assert.NotNull(log);
    }
}
