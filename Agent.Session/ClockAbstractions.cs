namespace Agent.Session;

public interface IClockStore
{
    Task<IReadOnlyList<ClockTask>> LoadAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClockTask>> ListAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<ClockTask?> GetAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task CreateAsync(
        ClockTask task,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        ClockTask task,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims one scheduled occurrence, advances the task, and creates a Running log.
    /// Implementations should make these changes atomic when their storage supports it.
    /// </summary>
    Task<ClockRunLog?> TryClaimAsync(
        ClockTask expectedTask,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? nextRunAtUtc,
        bool disableTask,
        CancellationToken cancellationToken = default);

    Task AppendRunLogAsync(
        ClockRunLog log,
        CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        ClockRunLog log,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes execution logs that were still running when the process stopped.
    /// </summary>
    Task RecoverInterruptedRunsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClockRunLog>> QueryLogsAsync(
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default);
}

public interface IClockExecutor
{
    Task<ClockExecutionResult> ExecuteAsync(
        ClockTask task,
        CancellationToken cancellationToken);
}

/// <summary>
/// 转发执行器：core 先以空转发器创建调度器，插件初始化完成后再注册自己的执行器（Inner）。
/// Inner 未注册时到点任务标记失败并记录原因，调度器不受影响。
/// </summary>
public sealed class DelegatingClockExecutor : IClockExecutor
{
    public IClockExecutor? Inner { get; set; }

    public Task<ClockExecutionResult> ExecuteAsync(ClockTask task, CancellationToken cancellationToken)
    {
        var inner = Inner;
        return inner == null
            ? Task.FromResult(ClockExecutionResult.Failure("定时任务执行器未注册（Agent 插件未加载）"))
            : inner.ExecuteAsync(task, cancellationToken);
    }
}
