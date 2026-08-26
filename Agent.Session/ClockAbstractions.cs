using System.Collections.Concurrent;

namespace Agent.Session;

public interface IClockStore
{
    Task<IReadOnlyList<ClockTask>> LoadAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClockTask>> ListAsync(
        string pluginId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<ClockTask?> GetAsync(
        string pluginId,
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
        string pluginId,
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
        string pluginId,
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
/// 按 pluginId 路由的转发执行器：core 先以空转发器集合创建调度器，各插件初始化时通过
/// <see cref="Add"/> 注册自己的执行器，执行时按 <see cref="ClockTask.PluginId"/> 路由。
/// 未注册插件的任务标记失败并记录原因，调度器不受影响；后注册者覆盖先前注册（返回旧执行器）。
/// </summary>
public sealed class DelegatingClockExecutor : IClockExecutor
{
    private readonly ConcurrentDictionary<string, IClockExecutor> _executors = new(StringComparer.Ordinal);

    /// <summary>注册插件执行器；返回被覆盖的旧执行器（无则 null）。</summary>
    public IClockExecutor? Add(string pluginId, IClockExecutor executor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(executor);
        return _executors.AddOrUpdate(pluginId, _ => executor, (_, _) => executor);
    }

    /// <summary>移除插件执行器；不存在返回 false。</summary>
    public bool Remove(string pluginId)
    {
        return _executors.TryRemove(pluginId, out _);
    }

    public Task<ClockExecutionResult> ExecuteAsync(ClockTask task, CancellationToken cancellationToken)
    {
        if (task.PluginId is { Length: > 0 } pluginId && _executors.TryGetValue(pluginId, out var executor))
        {
            return executor.ExecuteAsync(task, cancellationToken);
        }
        return Task.FromResult(ClockExecutionResult.Failure(
            $"定时任务执行器未注册（插件 {task.PluginId} 未加载）"));
    }
}
