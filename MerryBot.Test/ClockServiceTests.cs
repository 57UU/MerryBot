using Agent.Session;
using Microsoft.Extensions.Time.Testing;

namespace MerryBot.Test;

/// <summary>
/// ClockService 调度器单元测试：证明定时器启动、到点触发，以及各类"任务没有执行"的代码路径
/// （misfire 跳过、禁用、失败、超时）。
/// </summary>
public sealed class ClockServiceTests
{
    private static ClockService CreateService(
        FakeClockStore store,
        RecordingExecutor executor,
        FakeTimeProvider time)
    {
        return new ClockService(store, new DelegatingClockExecutor { Inner = executor }, time);
    }

    // ── 启动与触发 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Scheduler_Starts_And_Fires_Task_At_Due_Time()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        await TestClock.AdvanceUntilAsync(
            time,
            () => executor.CallCount > 0
                  && store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.Succeeded));

        // 到点后执行了一次，执行记录成功
        Assert.Single(executor.Executed);
        Assert.Equal(task.Id, executor.Executed[0].Id);
        var run = Assert.Single(store.SnapshotLogs());
        Assert.Equal(ClockRunStatus.Succeeded, run.Status);

        // 任务被推进到下一次执行，且保持启用
        var persisted = store.SnapshotTasks().Single(t => t.Id == task.Id);
        Assert.True(persisted.Enabled);
        Assert.True(persisted.NextRunAtUtc > TestClock.Start.AddMinutes(31));
    }

    [Fact]
    public async Task Scheduler_Does_Not_Fire_Before_Due_Time()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        // 先推进 29 分钟：不应触发
        time.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(200);
        Assert.Equal(0, executor.CallCount);
        Assert.Empty(store.SnapshotLogs());

        // 继续推进到 31 分钟：证明调度器本身是活的，确实会触发
        time.Advance(TimeSpan.FromMinutes(2));
        await TestClock.AdvanceUntilAsync(
            time,
            () => store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.Succeeded));
        Assert.Equal(1, executor.CallCount);
    }

    // ── run_once ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_Task_Disables_Itself_After_Firing()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", runOnce: true, nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        await TestClock.AdvanceUntilAsync(
            time,
            () => store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.Succeeded));

        Assert.Single(executor.Executed);

        var persisted = store.SnapshotTasks().Single(t => t.Id == task.Id);
        Assert.False(persisted.Enabled);
        Assert.Null(persisted.NextRunAtUtc);

        // 再推进很久也不会再执行
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(200);
        Assert.Equal(1, executor.CallCount);
    }

    // ── misfire：错过触发点的任务不会补跑 ─────────────────────────────────────

    [Fact]
    public async Task MissedOccurrence_OnStartup_IsSkipped_NotExecuted_ForRecurringTask()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        // NextRunAtUtc 已过期（1 小时前）
        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddHours(-1));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();
        await Task.Delay(200); // 等待启动调和完成

        // 执行器从未被调用：错过的执行被跳过，不补跑
        Assert.Empty(executor.Executed);

        var skipped = Assert.Single(store.SnapshotLogs());
        Assert.Equal(ClockRunStatus.Skipped, skipped.Status);
        Assert.Equal("misfire", skipped.SkipReason);
        Assert.Equal(TestClock.Start.AddHours(-1), skipped.ScheduledAtUtc);

        // 任务仍启用，已重排到下一次未来执行
        var persisted = store.SnapshotTasks().Single(t => t.Id == task.Id);
        Assert.True(persisted.Enabled);
        Assert.True(persisted.NextRunAtUtc > TestClock.Start);
    }

    [Fact]
    public async Task MissedOccurrence_OnStartup_IsSkipped_AndDisables_RunOnceTask()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", runOnce: true, nextRun: TestClock.Start.AddHours(-1));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();
        await Task.Delay(200);

        Assert.Empty(executor.Executed);
        var skipped = Assert.Single(store.SnapshotLogs());
        Assert.Equal(ClockRunStatus.Skipped, skipped.Status);
        Assert.Equal("misfire", skipped.SkipReason);

        // run_once 任务错过触发后直接被禁用，永不执行
        var persisted = store.SnapshotTasks().Single(t => t.Id == task.Id);
        Assert.False(persisted.Enabled);
        Assert.Null(persisted.NextRunAtUtc);
    }

    // ── 禁用 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_Task_Is_Not_Dispatched()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", enabled: false, nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        time.Advance(TimeSpan.FromMinutes(40));
        await Task.Delay(200);
        Assert.Equal(0, executor.CallCount);
        Assert.Empty(store.SnapshotLogs());
    }

    // ── 失败与超时 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Executor_Failure_Marks_Run_Failed_And_Scheduler_Keeps_Running()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor { Mode = RecordingExecutor.Behavior.Throw };
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        // 第一次触发：执行器抛异常 → failed
        await TestClock.AdvanceUntilAsync(
            time,
            () => store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.Failed));
        Assert.Equal(1, executor.CallCount);
        var failed = store.SnapshotLogs().Single(static l => l.Status == ClockRunStatus.Failed);
        Assert.NotNull(failed.Error);

        // 第二次触发（下一小时）：执行器恢复正常 → succeeded，调度器未被拖垮
        executor.Mode = RecordingExecutor.Behavior.Succeed;
        await TestClock.AdvanceUntilAsync(
            time,
            () => store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.Succeeded));
        Assert.Equal(2, executor.CallCount);
        Assert.Contains(store.SnapshotLogs(), static l => l.Status == ClockRunStatus.Succeeded);
    }

    [Fact]
    public async Task Execution_Timeout_Marks_Run_TimedOut()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var task = TestClock.MakeTask("qq:group:100", timeoutSeconds: 1, nextRun: TestClock.Start.AddMinutes(30));
        await store.CreateAsync(task);

        var executor = new RecordingExecutor { Mode = RecordingExecutor.Behavior.Hang };
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        // 执行挂起超过 TimeoutSeconds（真实等待 ≤1s）→ timedOut
        await TestClock.AdvanceUntilAsync(
            time,
            () => store.SnapshotLogs().Any(static l => l.Status == ClockRunStatus.TimedOut),
            step: TimeSpan.FromMinutes(1),
            maxSteps: 600);

        var timedOut = store.SnapshotLogs().Single(static l => l.Status == ClockRunStatus.TimedOut);
        Assert.Contains("超过", timedOut.Error);
    }

    // ── 会话隔离 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tasks_Are_Scoped_To_Their_Session()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        var created = await service.CreateAsync("qq:group:100", new ClockCreateRequest
        {
            CronExpression = "0 20 * * *",
            Content = "hi",
            Trigger = new ClockTrigger { Type = "group", Id = "100" },
        });

        // 其他会话不可见
        Assert.DoesNotContain(await service.ListAsync("qq:group:200"), t => t.Id == created.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync("qq:group:200", created.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateAsync("qq:group:200", created.Id, new ClockUpdateRequest { Content = "x" }));

        // 跨会话删除：服务层按会话所有权校验，抛 KeyNotFoundException
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync("qq:group:200", created.Id));
        Assert.NotNull(await service.GetAsync("qq:group:100", created.Id));

        // 本会话删除生效
        await service.DeleteAsync("qq:group:100", created.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync("qq:group:100", created.Id));
    }

    // ── 创建 / 更新校验 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Validates_Inputs()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        var trigger = new ClockTrigger { Type = "group", Id = "100" };

        // 非法 cron
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("qq:group:100",
            new ClockCreateRequest { CronExpression = "not a cron", Content = "hi", Trigger = trigger }));
        // 空 trigger
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("qq:group:100",
            new ClockCreateRequest { CronExpression = "0 0 * * *", Content = "hi", Trigger = new ClockTrigger() }));
        // 空会话
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("",
            new ClockCreateRequest { CronExpression = "0 0 * * *", Content = "hi", Trigger = trigger }));
        // 空内容
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("qq:group:100",
            new ClockCreateRequest { CronExpression = "0 0 * * *", Content = "  ", Trigger = trigger }));

        // @daily 别名 + 默认时区
        var daily = await service.CreateAsync("qq:group:100", new ClockCreateRequest
        {
            CronExpression = "@daily",
            Content = "hi",
            Trigger = trigger,
        });
        Assert.Equal("0 0 * * *", daily.CronExpression);
        Assert.Equal("Asia/Shanghai", daily.TimeZoneId);
        Assert.True(daily.Enabled);
        Assert.False(daily.RunOnce);
        Assert.NotNull(daily.NextRunAtUtc);
    }

    [Fact]
    public async Task Update_Recomputes_Schedule_And_Respects_Enable_Toggle()
    {
        var time = new FakeTimeProvider(TestClock.Start);
        var store = new FakeClockStore();
        var executor = new RecordingExecutor();
        await using var service = CreateService(store, executor, time);
        await service.StartAsync();

        var created = await service.CreateAsync("qq:group:100", new ClockCreateRequest
        {
            CronExpression = "0 20 * * *",
            Content = "hi",
            Trigger = new ClockTrigger { Type = "group", Id = "100" },
        });

        // 改 cron → 下次执行时间重算
        var changed = await service.UpdateAsync("qq:group:100", created.Id, new ClockUpdateRequest
        {
            CronExpression = "0 21 * * *",
        });
        Assert.Equal("0 21 * * *", changed.CronExpression);
        Assert.NotEqual(created.NextRunAtUtc, changed.NextRunAtUtc);

        // 只改内容 → 计划保持不变
        var contentOnly = await service.UpdateAsync("qq:group:100", created.Id, new ClockUpdateRequest
        {
            Content = "new content",
        });
        Assert.Equal(changed.NextRunAtUtc, contentOnly.NextRunAtUtc);
        Assert.Equal("new content", contentOnly.Content);

        // 禁用 → 下次执行为空
        var disabled = await service.UpdateAsync("qq:group:100", created.Id, new ClockUpdateRequest
        {
            Enabled = false,
        });
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextRunAtUtc);

        // 重新启用 → 重新计算下次执行
        var reEnabled = await service.UpdateAsync("qq:group:100", created.Id, new ClockUpdateRequest
        {
            Enabled = true,
        });
        Assert.True(reEnabled.Enabled);
        Assert.True(reEnabled.NextRunAtUtc > time.GetUtcNow());
    }
}
