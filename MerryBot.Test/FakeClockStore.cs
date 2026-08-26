using Agent.Session;

namespace MerryBot.Test;

/// <summary>
/// 进程内 <see cref="IClockStore"/> 实现：镜像真实存储的领取语义（CAS）与日志记录，
/// 供调度器单元测试使用。
/// </summary>
public sealed class FakeClockStore : IClockStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ClockTask> _tasks = new();
    private readonly Dictionary<Guid, ClockRunLog> _runLogs = new();

    /// <summary>当前全部任务快照（副本）。</summary>
    public IReadOnlyList<ClockTask> SnapshotTasks()
    {
        lock (_lock)
        {
            return _tasks.Values.Select(static t => t.Clone()).ToList();
        }
    }

    /// <summary>当前全部执行记录快照（副本），按计划时间升序。</summary>
    public IReadOnlyList<ClockRunLog> SnapshotLogs()
    {
        lock (_lock)
        {
            return _runLogs.Values
                .Select(static l => l.Clone())
                .OrderBy(static l => l.ScheduledAtUtc)
                .ToList();
        }
    }

    public Task EnsureInitializedAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<ClockTask>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ClockTask>>(
                _tasks.Values.Select(static t => t.Clone()).ToList());
        }
    }

    public Task<IReadOnlyList<ClockTask>> ListAsync(string pluginId, string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ClockTask>>(
                _tasks.Values
                    .Where(t => t.PluginId == pluginId && t.SessionId == sessionId)
                    .OrderBy(static t => t.CreatedAtUtc)
                    .Select(static t => t.Clone())
                    .ToList());
        }
    }

    public Task<ClockTask?> GetAsync(string pluginId, string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(
                _tasks.TryGetValue(taskId, out var task) && task.PluginId == pluginId && task.SessionId == sessionId
                    ? task.Clone()
                    : null);
        }
    }

    public Task CreateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_tasks.ContainsKey(task.Id))
            {
                throw new InvalidOperationException($"定时任务已存在: {task.Id}");
            }
            _tasks[task.Id] = task.Clone();
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_tasks.ContainsKey(task.Id))
            {
                throw new KeyNotFoundException($"未找到定时任务: {task.Id}");
            }
            _tasks[task.Id] = task.Clone();
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string pluginId, string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(taskId, out var task) &&
                task.PluginId == pluginId && task.SessionId == sessionId)
            {
                _tasks.Remove(taskId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<ClockRunLog?> TryClaimAsync(
        ClockTask expectedTask,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? nextRunAtUtc,
        bool disableTask,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_tasks.TryGetValue(expectedTask.Id, out var task)
                || task.PluginId != expectedTask.PluginId
                || task.SessionId != expectedTask.SessionId
                || !task.Enabled
                || task.NextRunAtUtc != expectedTask.NextRunAtUtc
                || task.NextRunAtUtc != scheduledAtUtc)
            {
                return Task.FromResult<ClockRunLog?>(null);
            }

            task.Enabled = !disableTask;
            task.NextRunAtUtc = nextRunAtUtc;
            task.LastRunAtUtc = scheduledAtUtc;
            task.UpdatedAtUtc = startedAtUtc;

            var log = new ClockRunLog
            {
                RunId = Guid.NewGuid(),
                TaskId = task.Id,
                PluginId = task.PluginId,
                SessionId = task.SessionId,
                ScheduledAtUtc = scheduledAtUtc,
                StartedAtUtc = startedAtUtc,
                Status = ClockRunStatus.Running,
            };
            _runLogs[log.RunId] = log.Clone();
            return Task.FromResult<ClockRunLog?>(log);
        }
    }

    public Task AppendRunLogAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _runLogs[log.RunId] = log.Clone();
        }
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_runLogs.ContainsKey(log.RunId))
            {
                throw new KeyNotFoundException($"未找到执行记录: {log.RunId}");
            }
            _runLogs[log.RunId] = log.Clone();
        }
        return Task.CompletedTask;
    }

    public Task RecoverInterruptedRunsAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var log in _runLogs.Values.Where(static l => l.Status == ClockRunStatus.Running).ToList())
            {
                log.Status = ClockRunStatus.Cancelled;
                log.FinishedAtUtc = recoveredAtUtc;
                log.Error = "服务重启前执行被中断";
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClockRunLog>> QueryLogsAsync(
        string pluginId,
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var limit = Math.Clamp(query.Limit, 1, 100);
            var result = _runLogs.Values
                .Where(l => l.PluginId == pluginId && l.SessionId == sessionId)
                .Where(l => query.TaskId == null || l.TaskId == query.TaskId)
                .Where(l => query.Status == null || l.Status == query.Status)
                .Where(l => query.FromUtc == null || l.ScheduledAtUtc >= query.FromUtc)
                .Where(l => query.ToUtc == null || l.ScheduledAtUtc <= query.ToUtc)
                .OrderByDescending(static l => l.ScheduledAtUtc)
                .Take(limit)
                .Select(static l => l.Clone())
                .ToList();
            return Task.FromResult<IReadOnlyList<ClockRunLog>>(result);
        }
    }
}
