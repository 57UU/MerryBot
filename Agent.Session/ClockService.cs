using CommonLib;
using Cronos;

namespace Agent.Session;

public sealed class ClockService : IAsyncDisposable
{
    private const int DefaultTimeoutSeconds = 600;
    private const int MaxTimeoutSeconds = 24 * 60 * 60;
    private const int ResultSummaryLimit = 2000;

    private readonly IClockStore _store;
    private readonly DelegatingClockExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly ISimpleLogger _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, ClockTask> _tasks = new();
    private readonly HashSet<Guid> _runningTasks = new();
    private readonly Dictionary<Guid, Task> _activeRuns = new();

    private Task? _schedulerTask;
    private bool _started;
    private bool _disposed;

    public ClockService(
        IClockStore store,
        DelegatingClockExecutor executor,
        TimeProvider? timeProvider = null,
        ISimpleLogger? logger = null)
    {
        _store = store;
        _executor = executor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? SimpleLog.Default;
    }

    /// <summary>调度器持有的执行器；宿主/插件通过设置其 <c>Inner</c> 注册真正的执行逻辑。</summary>
    public DelegatingClockExecutor Executor => _executor;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            await _store.RecoverInterruptedRunsAsync(now, cancellationToken);
            var loaded = await _store.LoadAllAsync(cancellationToken);
            foreach (var loadedTask in loaded)
            {
                try
                {
                    ValidateStoredTask(loadedTask);
                    var task = loadedTask.Clone();
                    _tasks[task.Id] = task;
                    await ReconcileLoadedTaskAsync(task, now, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // 启动被取消：不吞掉
                }
                catch (Exception ex)
                {
                    // 单个坏任务不影响其余任务加载：记日志并跳过
                    _logger.Warn($"加载定时任务失败，已跳过: {loadedTask.Id} - {ex.Message}");
                    _tasks.Remove(loadedTask.Id);
                }
            }

            _started = true;
            _schedulerTask = RunSchedulerAsync(_shutdown.Token);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? schedulerTask;
        Task[] activeTasks;

        await _stateLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shutdown.Cancel();
            SignalScheduler();
            schedulerTask = _schedulerTask;
            activeTasks = _activeRuns.Values.ToArray();
        }
        finally
        {
            _stateLock.Release();
        }

        if (schedulerTask != null)
        {
            await IgnoreCancellationAsync(schedulerTask);
        }

        if (activeTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(activeTasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                // The host is shutting down. An executor that does not observe
                // cancellation must not prevent the process from stopping.
            }
        }

        _shutdown.Dispose();
        _wakeSignal.Dispose();
        _stateLock.Dispose();
    }

    public async Task<ClockTask> CreateAsync(
        string sessionId,
        ClockCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        var expression = ClockSchedule.Normalize(request.CronExpression);
        var timezoneId = NormalizeTimeZoneId(request.TimeZoneId);
        _ = ResolveTimeZone(timezoneId);
        var content = RequireContent(request.Content);
        var trigger = ValidateTrigger(request.Trigger);
        var timeout = ValidateTimeout(request.TimeoutSeconds ?? DefaultTimeoutSeconds);
        var now = _timeProvider.GetUtcNow();

        var task = new ClockTask
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            CronExpression = expression,
            TimeZoneId = timezoneId,
            Content = content,
            Trigger = trigger,
            RunOnce = request.RunOnce ?? false,
            TimeoutSeconds = timeout,
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        task.NextRunAtUtc = GetNextOccurrence(task, now);

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            await _store.CreateAsync(task, cancellationToken);
            _tasks.Add(task.Id, task.Clone());
        }
        finally
        {
            _stateLock.Release();
        }
        SignalScheduler();
        return task.Clone();
    }

    public async Task<IReadOnlyList<ClockTask>> ListAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            return _tasks.Values
                .Where(x => x.SessionId == sessionId)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.Clone())
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClockTask> GetAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            return GetOwnedTask(sessionId, taskId).Clone();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClockTask> UpdateAsync(
        string sessionId,
        Guid taskId,
        ClockUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            var task = GetOwnedTask(sessionId, taskId);
            var scheduleChanged = false;

            if (request.CronExpression != null)
            {
                task.CronExpression = ClockSchedule.Normalize(request.CronExpression);
                task.ParsedCron = null; // 表达式已变更，失效解析缓存
                scheduleChanged = true;
            }
            if (request.TimeZoneId != null)
            {
                task.TimeZoneId = NormalizeTimeZoneId(request.TimeZoneId);
                _ = ResolveTimeZone(task.TimeZoneId);
                scheduleChanged = true;
            }
            if (request.Content != null)
            {
                task.Content = RequireContent(request.Content);
            }
            if (request.Trigger != null)
            {
                task.Trigger = ValidateTrigger(request.Trigger);
            }
            if (request.RunOnce.HasValue)
            {
                task.RunOnce = request.RunOnce.Value;
                scheduleChanged = true;
            }
            if (request.TimeoutSeconds.HasValue)
            {
                task.TimeoutSeconds = ValidateTimeout(request.TimeoutSeconds.Value);
            }
            if (request.Enabled.HasValue)
            {
                task.Enabled = request.Enabled.Value;
                scheduleChanged = true;
            }

            var now = _timeProvider.GetUtcNow();
            if (!task.Enabled)
            {
                task.NextRunAtUtc = null;
            }
            else if (scheduleChanged || task.NextRunAtUtc == null || task.NextRunAtUtc <= now)
            {
                task.NextRunAtUtc = GetNextOccurrence(task, now);
            }
            task.UpdatedAtUtc = now;

            await _store.UpdateAsync(task, cancellationToken);
            _tasks[task.Id] = task.Clone();
            var result = task.Clone();
            SignalScheduler();
            return result;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task DeleteAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            _ = GetOwnedTask(sessionId, taskId);
            await _store.DeleteAsync(sessionId, taskId, cancellationToken);
            _tasks.Remove(taskId);
        }
        finally
        {
            _stateLock.Release();
        }
        SignalScheduler();
    }

    public async Task<IReadOnlyList<ClockRunLog>> QueryLogsAsync(
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionId(sessionId);
        var normalizedQuery = new ClockLogQuery
        {
            TaskId = query.TaskId,
            Status = query.Status,
            FromUtc = query.FromUtc,
            ToUtc = query.ToUtc,
            Limit = Math.Clamp(query.Limit, 1, 100),
        };
        return await _store.QueryLogsAsync(sessionId, normalizedQuery, cancellationToken);
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset? nextRun;
                await _stateLock.WaitAsync(cancellationToken);
                try
                {
                    nextRun = _tasks.Values
                        .Where(x => x.Enabled && x.NextRunAtUtc.HasValue)
                        .Select(x => x.NextRunAtUtc)
                        .Min();
                }
                finally
                {
                    _stateLock.Release();
                }

                if (nextRun == null)
                {
                    await WaitForSignalOrDelayAsync(TimeSpan.FromMinutes(1), cancellationToken);
                    continue;
                }

                var delay = nextRun.Value - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await WaitForSignalOrDelayAsync(delay, cancellationToken);
                    continue;
                }

                await DispatchDueTasksAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break; // 正常停止
            }
            catch (Exception ex)
            {
                // 调度循环兜底：单次异常（存储/解析等）不杀死调度器，记录后继续下一轮
                _logger.Error($"调度循环异常: {ex}");
            }
        }
    }

    private async Task DispatchDueTasksAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        List<ClockTask> dueTasks;

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            dueTasks = _tasks.Values
                .Where(x => x.Enabled && x.NextRunAtUtc is { } next && next <= now)
                .Select(x => x.Clone())
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var task in dueTasks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ClaimAndStartAsync(task, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个任务失败（GetNextOccurrence/存储操作）记日志并跳过，循环继续
                _logger.Warn($"定时任务调度失败，已跳过 {task.Id}: {ex.Message}");
            }
        }
    }

    private async Task ClaimAndStartAsync(
        ClockTask task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await ClaimAndStartCoreAsync(task, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 停机/取消：不当作任务失败
        }
        catch (Exception ex)
        {
            // 单个任务异常（GetNextOccurrence/存储操作）记日志并跳过，调度循环继续
            _logger.Warn($"定时任务领取失败，已跳过 {task.Id}: {ex.Message}");
        }
    }

    private async Task ClaimAndStartCoreAsync(
        ClockTask task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ClockRunLog? run = null;
        ClockTask? executionTask = null;

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tasks.TryGetValue(task.Id, out var current) ||
                !current.Enabled ||
                current.NextRunAtUtc != task.NextRunAtUtc)
            {
                return;
            }

            var scheduledAt = current.NextRunAtUtc!.Value;
            if (_runningTasks.Contains(current.Id))
            {
                DateTimeOffset? next = current.RunOnce ? null : GetNextOccurrence(current, now);
                var skipped = MakeSkippedLog(current, scheduledAt, "overlap", now);
                current.Enabled = !current.RunOnce;
                current.NextRunAtUtc = next;
                current.LastRunAtUtc = scheduledAt;
                current.UpdatedAtUtc = now;
                await _store.UpdateAsync(current, cancellationToken);
                await _store.AppendRunLogAsync(skipped, cancellationToken);
                _tasks[current.Id] = current.Clone();
                return;
            }

            DateTimeOffset? nextRun = current.RunOnce ? null : GetNextOccurrence(current, now);
            executionTask = current.Clone();
            run = await _store.TryClaimAsync(
                current,
                scheduledAt,
                now,
                nextRun,
                current.RunOnce,
                cancellationToken);
            if (run == null)
            {
                return;
            }

            current.Enabled = !current.RunOnce;
            current.NextRunAtUtc = nextRun;
            current.LastRunAtUtc = scheduledAt;
            _tasks[current.Id] = current.Clone();
            _runningTasks.Add(current.Id);
        }
        finally
        {
            _stateLock.Release();
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // The claim already happened. Registration must complete even when
        // shutdown races with this hand-off, otherwise the claimed run would
        // never receive a terminal log.
        await _stateLock.WaitAsync();
        try
        {
            _activeRuns[run!.RunId] = completion.Task;
        }
        finally
        {
            _stateLock.Release();
        }
        _ = ExecuteClaimAsync(executionTask!, run!, completion, cancellationToken);
    }

    private async Task ExecuteClaimAsync(
        ClockTask task,
        ClockRunLog run,
        TaskCompletionSource completion,
        CancellationToken schedulerCancellationToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            schedulerCancellationToken,
            _shutdown.Token);
        executionCancellation.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

        try
        {
            var result = await _executor.ExecuteAsync(task, executionCancellation.Token);
            run.Status = result.Succeeded ? ClockRunStatus.Succeeded : ClockRunStatus.Failed;
            run.ResultSummary = Truncate(result.ResultSummary);
            run.Error = result.Succeeded ? null : Truncate(result.Error);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            run.Status = ClockRunStatus.Cancelled;
            run.Error = "调度器已停止";
        }
        catch (OperationCanceledException)
        {
            run.Status = ClockRunStatus.TimedOut;
            run.Error = $"任务执行超过 {task.TimeoutSeconds} 秒";
        }
        catch (Exception ex)
        {
            run.Status = ClockRunStatus.Failed;
            run.Error = Truncate(ex.Message);
        }
        finally
        {
            run.FinishedAtUtc = _timeProvider.GetUtcNow();
            try
            {
                await _store.CompleteRunAsync(run, CancellationToken.None);
            }
            finally
            {
                await _stateLock.WaitAsync();
                try
                {
                    _runningTasks.Remove(task.Id);
                    _activeRuns.Remove(run.RunId);
                }
                finally
                {
                    _stateLock.Release();
                }
                completion.TrySetResult();
                SignalScheduler();
            }
        }
    }

    private async Task ReconcileLoadedTaskAsync(
        ClockTask task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!task.Enabled)
        {
            return;
        }

        if (task.NextRunAtUtc is { } next && next <= now)
        {
            var skipped = MakeSkippedLog(task, next, "misfire", now);
            await _store.AppendRunLogAsync(skipped, cancellationToken);
            task.LastRunAtUtc = next;
            task.Enabled = !task.RunOnce;
            task.NextRunAtUtc = task.RunOnce ? null : GetNextOccurrence(task, now);
            task.UpdatedAtUtc = now;
            await _store.UpdateAsync(task, cancellationToken);
        }
        else if (task.NextRunAtUtc == null)
        {
            task.NextRunAtUtc = GetNextOccurrence(task, now);
            task.UpdatedAtUtc = now;
            await _store.UpdateAsync(task, cancellationToken);
        }
    }

    private ClockTask GetOwnedTask(string sessionId, Guid taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task) || task.SessionId != sessionId)
        {
            throw new KeyNotFoundException($"当前会话未找到定时任务: {taskId}");
        }
        return task;
    }

    private DateTimeOffset GetNextOccurrence(ClockTask task, DateTimeOffset fromUtc)
    {
        // 缓存 CronExpression 解析结果，避免每次调度重复解析
        var expression = task.ParsedCron
            ??= CronExpression.Parse(task.CronExpression, CronFormat.Standard);
        var timezone = ResolveTimeZone(task.TimeZoneId);
        return expression.GetNextOccurrence(fromUtc, timezone)
            ?? throw new InvalidOperationException("Cron 表达式没有可计算的下一次执行时间");
    }

    private static ClockRunLog MakeSkippedLog(
        ClockTask task,
        DateTimeOffset scheduledAtUtc,
        string reason,
        DateTimeOffset now)
    {
        return new ClockRunLog
        {
            RunId = Guid.NewGuid(),
            TaskId = task.Id,
            SessionId = task.SessionId,
            ScheduledAtUtc = scheduledAtUtc,
            StartedAtUtc = now,
            FinishedAtUtc = now,
            Status = ClockRunStatus.Skipped,
            SkipReason = reason,
        };
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _wakeSignal.WaitAsync(waitCancellation.Token);
        // Task.Delay 单次上限约 49.7 天（int.MaxValue 毫秒）：拆成不超过 24 小时的片段，
        // 每段都与信号竞争，信号到来立即退出。
        while (delay > TimeSpan.Zero)
        {
            var chunk = delay > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : delay;
            var delayTask = Task.Delay(chunk, _timeProvider, cancellationToken);
            var winner = await Task.WhenAny(signalTask, delayTask);
            if (winner == signalTask)
            {
                await signalTask;
                return;
            }
            if (delayTask.IsCanceled)
            {
                break; // 外部取消：结束等待，由调度循环退出
            }
            delay -= chunk;
        }
        waitCancellation.Cancel();
        await IgnoreCancellationAsync(signalTask);
    }

    private void SignalScheduler()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending signal is enough to wake the scheduler.
        }
        catch (ObjectDisposedException)
        {
            // Disposal is already in progress.
        }
    }

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException("ClockService 尚未 StartAsync");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClockService));
        }
    }

    private static void EnsureSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }
    }

    private static string RequireContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("任务内容不能为空");
        }
        return content.Trim();
    }

    private static ClockTrigger ValidateTrigger(ClockTrigger trigger)
    {
        if (trigger == null || string.IsNullOrWhiteSpace(trigger.Type) || string.IsNullOrWhiteSpace(trigger.Id))
        {
            throw new ArgumentException("trigger.type 和 trigger.id 不能为空");
        }
        return trigger.Clone();
    }

    private static int ValidateTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                $"超时必须在 1 到 {MaxTimeoutSeconds} 秒之间");
        }
        return timeoutSeconds;
    }

    private static readonly IReadOnlyDictionary<string, string> IanaToWindowsTimeZones =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UTC"] = "UTC",
            ["Etc/UTC"] = "UTC",
            ["Etc/GMT"] = "GMT Standard Time",
            ["Asia/Shanghai"] = "China Standard Time",
            ["Asia/Chongqing"] = "China Standard Time",
            ["Asia/Harbin"] = "China Standard Time",
            ["Asia/Hong_Kong"] = "China Standard Time",
            ["Asia/Macau"] = "China Standard Time",
            ["Asia/Taipei"] = "Taipei Standard Time",
            ["Asia/Seoul"] = "Korea Standard Time",
            ["Asia/Tokyo"] = "Tokyo Standard Time",
            ["Asia/Singapore"] = "Singapore Standard Time",
            ["Asia/Kolkata"] = "India Standard Time",
            ["Asia/Bangkok"] = "SE Asia Standard Time",
            ["Asia/Jakarta"] = "SE Asia Standard Time",
            ["Asia/Dubai"] = "Arabian Standard Time",
            ["Asia/Tehran"] = "Iran Standard Time",
            ["Asia/Jerusalem"] = "Israel Standard Time",
            ["Europe/London"] = "GMT Standard Time",
            ["Europe/Paris"] = "W. Europe Standard Time",
            ["Europe/Berlin"] = "W. Europe Standard Time",
            ["Europe/Rome"] = "W. Europe Standard Time",
            ["Europe/Amsterdam"] = "W. Europe Standard Time",
            ["Europe/Zurich"] = "W. Europe Standard Time",
            ["Europe/Vienna"] = "W. Europe Standard Time",
            ["Europe/Madrid"] = "Romance Standard Time",
            ["Europe/Brussels"] = "Romance Standard Time",
            ["Europe/Stockholm"] = "W. Europe Standard Time",
            ["Europe/Warsaw"] = "Central European Standard Time",
            ["Europe/Prague"] = "Central European Standard Time",
            ["Europe/Athens"] = "GTB Standard Time",
            ["Europe/Helsinki"] = "FLE Standard Time",
            ["Europe/Moscow"] = "Russian Standard Time",
            ["America/New_York"] = "Eastern Standard Time",
            ["America/Chicago"] = "Central Standard Time",
            ["America/Denver"] = "Mountain Standard Time",
            ["America/Los_Angeles"] = "Pacific Standard Time",
            ["America/Phoenix"] = "US Mountain Standard Time",
            ["America/Anchorage"] = "Alaskan Standard Time",
            ["America/Toronto"] = "Eastern Standard Time",
            ["America/Vancouver"] = "Pacific Standard Time",
            ["America/Sao_Paulo"] = "E. South America Standard Time",
            ["America/Mexico_City"] = "Central Standard Time (Mexico)",
            ["America/Bogota"] = "SA Pacific Standard Time",
            ["America/Lima"] = "SA Pacific Standard Time",
            ["America/Argentina/Buenos_Aires"] = "Argentina Standard Time",
            ["Australia/Sydney"] = "AUS Eastern Standard Time",
            ["Australia/Melbourne"] = "AUS Eastern Standard Time",
            ["Australia/Perth"] = "W. Australia Standard Time",
            ["Pacific/Auckland"] = "New Zealand Standard Time",
            ["Pacific/Honolulu"] = "Hawaiian Standard Time",
        };

    private TimeZoneInfo ResolveTimeZone(string? timezoneId)
    {
        var id = NormalizeTimeZoneId(timezoneId);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return ResolveTimeZoneByWindowsName(id);
        }
        catch (InvalidTimeZoneException)
        {
            return ResolveTimeZoneByWindowsName(id);
        }
    }

    /// <summary>
    /// Windows 缺少 IANA 时区数据时，按常见等价名映射表查找 Windows 时区；
    /// 仍失败则回退 UTC 并记日志。
    /// </summary>
    private TimeZoneInfo ResolveTimeZoneByWindowsName(string ianaId)
    {
        if (IanaToWindowsTimeZones.TryGetValue(ianaId, out var windowsId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        _logger.Warn($"未找到时区 {ianaId}，回退到 UTC");
        return TimeZoneInfo.Utc;
    }

    private static string NormalizeTimeZoneId(string? timezoneId)
    {
        return string.IsNullOrWhiteSpace(timezoneId) ? "Asia/Shanghai" : timezoneId.Trim();
    }

    private void ValidateStoredTask(ClockTask task)
    {
        _ = ClockSchedule.Normalize(task.CronExpression);
        task.ParsedCron = CronExpression.Parse(task.CronExpression, CronFormat.Standard); // 加载时解析一次并缓存
        _ = ResolveTimeZone(task.TimeZoneId);
        _ = RequireContent(task.Content);
        _ = ValidateTrigger(task.Trigger);
        _ = ValidateTimeout(task.TimeoutSeconds);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= ResultSummaryLimit)
        {
            return value;
        }
        return value[..ResultSummaryLimit] + "…";
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal static class ClockSchedule
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["@yearly"] = "0 0 1 1 *",
            ["@annually"] = "0 0 1 1 *",
            ["@monthly"] = "0 0 1 * *",
            ["@weekly"] = "0 0 * * 0",
            ["@daily"] = "0 0 * * *",
            ["@midnight"] = "0 0 * * *",
            ["@hourly"] = "0 * * * *",
        };

    public static string Normalize(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("cron 表达式不能为空");
        }

        var value = expression.Trim();
        if (Aliases.TryGetValue(value, out var alias))
        {
            value = alias;
        }

        var fields = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new ArgumentException("只支持 Linux Cron 五字段格式: 分 时 日 月 周");
        }

        _ = CronExpression.Parse(value, CronFormat.Standard);
        return string.Join(' ', fields);
    }
}
