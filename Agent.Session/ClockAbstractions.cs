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
