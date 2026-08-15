using Agent.Session;
using DataProvider;
using Microsoft.Extensions.Time.Testing;

namespace MerryBot.Test;

/// <summary>
/// 真实 LiteDB 存储 + 真实调度器（ClockService）的端到端集成测试：
/// 完整复现"任务到点执行"链路（写入 → 启动加载 → 调度 → CAS 领取 → 执行 → 完成日志），
/// 验证 CoreClockStore 的 UTC 往返修复后领取不再被静默拒绝。
/// </summary>
public sealed class ClockServiceStoreIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PluginStorageDatabase _db;
    private readonly CoreClockStore _store;

    public ClockServiceStoreIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"merrybot-e2e-{Guid.NewGuid():N}.db");
        _db = new PluginStorageDatabase(_dbPath);
        _store = new CoreClockStore(_db.CreateScope("clock", prefix: "core"));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var suffix in new[] { "", "-log", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 文件被占用则留给系统回收
                }
            }
        }
    }

    private async Task<bool> HasSucceededRunAsync(Guid taskId)
    {
        var logs = await _store.QueryLogsAsync("qq:group:100", new ClockLogQuery { TaskId = taskId, Limit = 10 });
        return logs.Any(static l => l.Status == ClockRunStatus.Succeeded);
    }

    [Fact]
    public async Task Full_Chain_Fires_Task_At_Due_Time_With_Real_LiteDb()
    {
        await _store.EnsureInitializedAsync();
        var time = new FakeTimeProvider(TestClock.Start);
        var executor = new RecordingExecutor();

        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddMinutes(30));
        await _store.CreateAsync(task);

        await using var service = new ClockService(_store, new DelegatingClockExecutor { Inner = executor }, time);
        await service.StartAsync();

        await TestClock.AdvanceUntilAsync(
            time,
            async () => executor.CallCount > 0 && await HasSucceededRunAsync(task.Id));

        // 执行了一次且执行记录成功（完整链路走通：领取未被 CAS 拒绝）
        Assert.Single(executor.Executed);
        Assert.Equal(task.Id, executor.Executed[0].Id);
        var logs = await _store.QueryLogsAsync("qq:group:100", new ClockLogQuery { TaskId = task.Id, Limit = 10 });
        var run = Assert.Single(logs);
        Assert.Equal(ClockRunStatus.Succeeded, run.Status);

        // 任务被推进到下一次执行
        var persisted = await _store.GetAsync("qq:group:100", task.Id);
        Assert.True(persisted!.Enabled);
        Assert.True(persisted.NextRunAtUtc > TestClock.Start.AddMinutes(31));
    }

    [Fact]
    public async Task Restart_Loads_Persisted_Schedule_And_Fires_Again()
    {
        await _store.EnsureInitializedAsync();
        var time = new FakeTimeProvider(TestClock.Start);
        var executor = new RecordingExecutor();

        var task = TestClock.MakeTask("qq:group:100", nextRun: TestClock.Start.AddMinutes(30));
        await _store.CreateAsync(task);

        // 第一次运行：到点触发，任务推进到下一个小时
        {
            await using var service = new ClockService(_store, new DelegatingClockExecutor { Inner = executor }, time);
            await service.StartAsync();
            await TestClock.AdvanceUntilAsync(
                time,
                async () => executor.CallCount > 0 && await HasSucceededRunAsync(task.Id));
            Assert.Equal(1, executor.CallCount);
        } // 模拟进程退出

        // 重启：重新加载持久化任务，NextRunAtUtc 必须与运行期完全一致（UTC 往返无损）
        await using var restarted = new ClockService(_store, new DelegatingClockExecutor { Inner = executor }, time);
        await restarted.StartAsync();
        var loaded = await _store.GetAsync("qq:group:100", task.Id);
        Assert.Equal(TestClock.Start.AddMinutes(60), loaded!.NextRunAtUtc);

        // 再次触发下一小时 → 成功
        await TestClock.AdvanceUntilAsync(
            time,
            async () => executor.CallCount >= 2 && await HasSucceededRunAsync(task.Id));
        Assert.Equal(2, executor.CallCount);
        var logs = await _store.QueryLogsAsync("qq:group:100", new ClockLogQuery { TaskId = task.Id, Limit = 10 });
        Assert.Equal(2, logs.Count(l => l.Status == ClockRunStatus.Succeeded));
    }
}
