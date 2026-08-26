namespace Agent.Session;

/// <summary>
/// Small in-memory implementation for tests and local composition.
/// Production persistence can implement IClockStore without changing Cron.
/// </summary>
public sealed class InMemoryClockStore : IClockStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ClockTask> _tasks = new();
    private readonly Dictionary<Guid, ClockRunLog> _logs = new();

    public Task<IReadOnlyList<ClockTask>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<ClockTask>>(_tasks.Values.Select(x => x.Clone()).ToList());
        }
    }

    public Task<IReadOnlyList<ClockTask>> ListAsync(string pluginId, string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<ClockTask>>(_tasks.Values
                .Where(x => x.PluginId == pluginId && x.SessionId == sessionId)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.Clone())
                .ToList());
        }
    }

    public Task<ClockTask?> GetAsync(string pluginId, string sessionId, Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_tasks.TryGetValue(taskId, out var task) &&
                    task.PluginId == pluginId && task.SessionId == sessionId
                ? task.Clone()
                : null);
        }
    }

    public Task CreateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_tasks.TryAdd(task.Id, task.Clone()))
            {
                throw new InvalidOperationException($"定时任务已存在: {task.Id}");
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ClockTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_tasks.TryGetValue(expectedTask.Id, out var task) ||
                task.PluginId != expectedTask.PluginId ||
                task.SessionId != expectedTask.SessionId ||
                !task.Enabled ||
                task.NextRunAtUtc != expectedTask.NextRunAtUtc ||
                task.NextRunAtUtc != scheduledAtUtc)
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
            _logs[log.RunId] = log.Clone();
            return Task.FromResult<ClockRunLog?>(log);
        }
    }

    public Task AppendRunLogAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _logs[log.RunId] = log.Clone();
        }
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(ClockRunLog log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_logs.ContainsKey(log.RunId))
            {
                throw new KeyNotFoundException($"未找到执行记录: {log.RunId}");
            }
            _logs[log.RunId] = log.Clone();
        }
        return Task.CompletedTask;
    }

    public Task RecoverInterruptedRunsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (var log in _logs.Values.Where(item => item.Status == ClockRunStatus.Running))
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
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 100);
        lock (_sync)
        {
            var result = _logs.Values
                .Where(x => x.PluginId == pluginId && x.SessionId == sessionId)
                .Where(x => query.TaskId == null || x.TaskId == query.TaskId)
                .Where(x => query.Status == null || x.Status == query.Status)
                .Where(x => query.FromUtc == null || x.ScheduledAtUtc >= query.FromUtc)
                .Where(x => query.ToUtc == null || x.ScheduledAtUtc <= query.ToUtc)
                .OrderByDescending(x => x.ScheduledAtUtc)
                .Take(limit)
                .Select(x => x.Clone())
                .ToList();
            return Task.FromResult<IReadOnlyList<ClockRunLog>>(result);
        }
    }
}
