using Agent.Session;

namespace MerryBot.Test;

/// <summary>
/// 记录式执行器：记录被执行的次数与任务，支持三种行为模式（成功 / 抛异常 / 挂起）。
/// </summary>
public sealed class RecordingExecutor : IClockExecutor
{
    public enum Behavior
    {
        Succeed,
        Throw,
        Hang,
    }

    private readonly List<ClockTask> _executed = new();
    private readonly List<TaskCompletionSource<ClockExecutionResult>> _hangs = new();
    private readonly object _lock = new();

    /// <summary>当前行为模式；测试可在执行间隙切换。</summary>
    public Behavior Mode { get; set; } = Behavior.Succeed;

    /// <summary>Throw 模式抛出的异常；默认 InvalidOperationException。</summary>
    public Exception ThrowException { get; set; } = new InvalidOperationException("executor failure");

    public IReadOnlyList<ClockTask> Executed
    {
        get
        {
            lock (_lock)
            {
                return _executed.Select(static t => t.Clone()).ToList();
            }
        }
    }

    public int CallCount
    {
        get
        {
            lock (_lock)
            {
                return _executed.Count;
            }
        }
    }

    public Task<ClockExecutionResult> ExecuteAsync(ClockTask task, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _executed.Add(task.Clone());
        }

        var mode = Mode;
        if (mode == Behavior.Throw)
        {
            throw ThrowException;
        }
        if (mode == Behavior.Hang)
        {
            var tcs = new TaskCompletionSource<ClockExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            lock (_lock)
            {
                _hangs.Add(tcs);
            }
            return tcs.Task;
        }
        return Task.FromResult(ClockExecutionResult.Success("ok"));
    }
}
