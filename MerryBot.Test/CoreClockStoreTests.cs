using Agent.Session;
using DataProvider;
using LiteDB;

namespace MerryBot.Test;

/// <summary>
/// CoreClockStore（LiteDB 真实存储）集成测试：验证领取的原子性（CAS）与中断恢复。
/// </summary>
public sealed class CoreClockStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PluginStorageDatabase _db;
    private readonly CoreClockStore _store;

    public CoreClockStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"merrybot-test-{Guid.NewGuid():N}.db");
        _db = new PluginStorageDatabase(_dbPath);
        _store = new CoreClockStore(_db.CreateScope("clock", prefix: "core"));
    }

    public void Dispose()
    {
        _db.Dispose();
        // 清理 LiteDB 生成的文件
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

    [Fact]
    public async Task DateTime_Round_Trip_Preserves_Utc_Instant()
    {
        // 1) 连接已启用 UTC_DATE pragma：读回必须保持 Kind=Utc、数值不变。
        //    若该断言失败说明 pragma 被移除——LiteDB 默认把 DateTime 读成本地墙钟（本机 +8），
        //    正是 CAS 领取被静默拒绝、任务不执行的根因，固定断言防止回归。
        var scope = _db.CreateScope("clock", prefix: "core");
        var raw = scope.GetCollection<RawDateTimeRecord>("raw_probe");
        await raw.InsertAsync(new RawDateTimeRecord
        {
            Id = Guid.NewGuid(),
            Utc = new DateTime(2026, 8, 15, 12, 30, 0, DateTimeKind.Utc),
        });
        var rawRead = (await raw.FindAllAsync()).Single();
        Assert.Equal(DateTimeKind.Utc, rawRead.Utc.Kind);
        Assert.Equal(new DateTime(2026, 8, 15, 12, 30, 0, DateTimeKind.Utc), rawRead.Utc);

        // 2) CoreClockStore 往返必须保持 UTC 实例不变
        await _store.EnsureInitializedAsync();
        var task = TestClock.MakeTask("qq:group:1", nextRun: TestClock.Start.AddMinutes(30));
        await _store.CreateAsync(task);

        var loaded = (await _store.LoadAllAsync()).Single();
        Assert.Equal(task.NextRunAtUtc, loaded.NextRunAtUtc);
    }

    private sealed class RawDateTimeRecord
    {
        [BsonId] public Guid Id { get; set; }
        public DateTime Utc { get; set; }
    }

    [Fact]
    public async Task TryClaim_Claims_Matching_Occurrence_Exactly_Once()
    {
        await _store.EnsureInitializedAsync();
        // 用秒级精度时间：LiteDB 存储 DateTime 时截断到毫秒，CAS 对比需无损往返
        var now = TestClock.Start;
        var task = TestClock.MakeTask("qq:group:1", nextRun: now.AddMinutes(30));
        await _store.CreateAsync(task);
        var scheduled = task.NextRunAtUtc!.Value;

        // 1. 首次领取成功：任务被推进，写入 Running 日志
        var claim1 = await _store.TryClaimAsync(task, scheduled, now, now.AddHours(1), disableTask: false);
        Assert.NotNull(claim1);
        Assert.Equal(ClockRunStatus.Running, claim1!.Status);
        Assert.Equal(task.Id, claim1.TaskId);

        var persisted = await _store.GetAsync(TestClock.PluginId, "qq:group:1", task.Id);
        Assert.Equal(now.AddHours(1), persisted!.NextRunAtUtc);
        Assert.Equal(scheduled, persisted.LastRunAtUtc);

        // 2. 用过期的期望状态再次领取 → 拒绝（CAS 生效）
        var staleExpected = task.Clone(); // 仍持有旧的 NextRunAtUtc
        var claim2 = await _store.TryClaimAsync(staleExpected, scheduled, now, now.AddHours(2), disableTask: false);
        Assert.Null(claim2);

        // 3. 用当前状态领取同一时刻（scheduledAt 与存储不一致）→ 拒绝
        var claim3 = await _store.TryClaimAsync(persisted, scheduled, now, now.AddHours(2), disableTask: false);
        Assert.Null(claim3);

        // 4. 并发领取同一任务：只有一个成功
        var task2 = TestClock.MakeTask("qq:group:1", nextRun: now.AddMinutes(5));
        await _store.CreateAsync(task2);
        var scheduled2 = task2.NextRunAtUtc!.Value;
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => _store.TryClaimAsync(task2, scheduled2, now, now.AddHours(1), disableTask: false)));
        Assert.Equal(1, results.Count(static r => r != null));
    }

    [Fact]
    public async Task TryClaim_Rejects_Disabled_Task()
    {
        await _store.EnsureInitializedAsync();
        var now = TestClock.Start;
        var task = TestClock.MakeTask("qq:group:1", enabled: false, nextRun: now.AddMinutes(30));
        await _store.CreateAsync(task);

        var claim = await _store.TryClaimAsync(task, task.NextRunAtUtc!.Value, now, now.AddHours(1), disableTask: false);
        Assert.Null(claim);
    }

    [Fact]
    public async Task CompleteRun_Updates_Existing_Log()
    {
        await _store.EnsureInitializedAsync();
        var now = TestClock.Start;
        var task = TestClock.MakeTask("qq:group:1", nextRun: now.AddMinutes(30));
        await _store.CreateAsync(task);

        var claim = await _store.TryClaimAsync(task, task.NextRunAtUtc!.Value, now, now.AddHours(1), disableTask: false);
        Assert.NotNull(claim);

        claim!.Status = ClockRunStatus.Succeeded;
        claim.FinishedAtUtc = now.AddMinutes(1);
        claim.ResultSummary = "ok";
        await _store.CompleteRunAsync(claim);

        var logs = await _store.QueryLogsAsync(TestClock.PluginId, "qq:group:1", new ClockLogQuery { TaskId = task.Id, Limit = 10 });
        var log = Assert.Single(logs);
        Assert.Equal(ClockRunStatus.Succeeded, log.Status);
        Assert.Equal("ok", log.ResultSummary);
    }

    [Fact]
    public async Task RecoverInterruptedRuns_Marks_Running_As_Cancelled()
    {
        await _store.EnsureInitializedAsync();
        var now = TestClock.Start;
        var task = TestClock.MakeTask("qq:group:1", nextRun: now.AddMinutes(30));
        await _store.CreateAsync(task);

        var claim = await _store.TryClaimAsync(task, task.NextRunAtUtc!.Value, now, null, disableTask: true);
        Assert.NotNull(claim);
        Assert.Equal(ClockRunStatus.Running, claim!.Status);

        // 模拟重启：Running 记录被标记为 Cancelled
        await _store.RecoverInterruptedRunsAsync(now.AddMinutes(10));

        var logs = await _store.QueryLogsAsync(TestClock.PluginId, "qq:group:1", new ClockLogQuery { TaskId = task.Id, Limit = 10 });
        var log = Assert.Single(logs);
        Assert.Equal(ClockRunStatus.Cancelled, log.Status);
        Assert.Contains("重启", log.Error);
        Assert.NotNull(log.FinishedAtUtc);
    }

    [Fact]
    public async Task EnsureInitialized_Throws_On_Unsupported_Schema_Version()
    {
        // 直接写入一个不受支持的 schema 版本，模拟存储损坏/版本不兼容的场景
        var scope = _db.CreateScope("clock", prefix: "core");
        var meta = scope.GetCollection<MetaRecord>("meta");
        await meta.InsertAsync(new MetaRecord
        {
            Id = "persistence-schema-version",
            Value = "999",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.EnsureInitializedAsync());
    }

    // ── Content object?：POCO 往返 / 类型已删除降级 ─────────────────────────

    private sealed class TestPayload
    {
        public string Topic { get; set; } = string.Empty;
        public int Limit { get; set; }
    }

    [Fact]
    public async Task Content_Poco_RoundTrips_Through_Storage()
    {
        await _store.EnsureInitializedAsync();
        var task = TestClock.MakeTask("qq:group:1", nextRun: TestClock.Start.AddMinutes(30));
        task.Content = new TestPayload { Topic = "report", Limit = 5 };
        await _store.CreateAsync(task);

        var loaded = (await _store.LoadAllAsync()).Single();
        var payload = Assert.IsType<TestPayload>(loaded.Content);
        Assert.Equal("report", payload.Topic);
        Assert.Equal(5, payload.Limit);

        // 按 pluginId+sessionId 列表读取同样还原
        var listed = (await _store.ListAsync(TestClock.PluginId, "qq:group:1")).Single();
        Assert.IsType<TestPayload>(listed.Content);
    }

    [Fact]
    public async Task Content_With_Removed_Type_Degrades_To_Text_Not_Exception()
    {
        await _store.EnsureInitializedAsync();
        // 手工插入 Content 指向不存在类型的文档，模拟插件被删除后的存量数据
        var scope = _db.CreateScope("clock", prefix: "core");
        var docs = scope.GetCollection<BsonDocument>("clock_tasks");
        var doc = new BsonDocument
        {
            ["_id"] = Guid.NewGuid(),
            ["PluginId"] = "removed-plugin",
            ["SessionId"] = "qq:group:1",
            ["CronExpression"] = "0 9 * * *",
            ["TimeZoneId"] = "UTC",
            ["Content"] = new BsonDocument
            {
                ["_type"] = "MerryBot.Test.DoesNotExistPayload, MerryBot.Test",
                ["Topic"] = "stale",
            },
            ["TriggerType"] = "group",
            ["TriggerId"] = "1",
            ["RunOnce"] = false,
            ["TimeoutSeconds"] = 600,
            ["Enabled"] = true,
            ["CreatedAtUtc"] = TestClock.Start.UtcDateTime,
            ["UpdatedAtUtc"] = TestClock.Start.UtcDateTime,
        };
        await docs.InsertAsync(doc);

        // 弱类型读取：不抛异常，Content 降级为 JSON 文本
        var loaded = (await _store.LoadAllAsync()).Single(t => t.PluginId == "removed-plugin");
        Assert.NotNull(loaded.Content);
        Assert.Contains("stale", loaded.Content.ToString());
    }

    // ── v1→v2 迁移：存量数据补 PluginId ────────────────────────────────────

    [Fact]
    public async Task EnsureInitialized_Migrates_V1_Records_To_Agent_PluginId()
    {
        // 构造 v1 存量：任务与日志都没有 PluginId，meta 版本为 "1"
        var scope = _db.CreateScope("clock", prefix: "core");
        var taskDocs = scope.GetCollection<BsonDocument>("clock_tasks");
        var logDocs = scope.GetCollection<BsonDocument>("clock_run_logs");
        var meta = scope.GetCollection<MetaRecord>("meta");

        var taskId = Guid.NewGuid();
        await taskDocs.InsertAsync(new BsonDocument
        {
            ["_id"] = taskId,
            ["SessionId"] = "qq:group:1",
            ["CronExpression"] = "0 9 * * *",
            ["TimeZoneId"] = "UTC",
            ["Content"] = "legacy",
            ["TriggerType"] = "group",
            ["TriggerId"] = "1",
            ["RunOnce"] = false,
            ["TimeoutSeconds"] = 600,
            ["Enabled"] = true,
            ["NextRunAtUtc"] = TestClock.Start.AddMinutes(30).UtcDateTime,
            ["CreatedAtUtc"] = TestClock.Start.UtcDateTime,
            ["UpdatedAtUtc"] = TestClock.Start.UtcDateTime,
        });
        await logDocs.InsertAsync(new BsonDocument
        {
            ["RunId"] = Guid.NewGuid(),
            ["TaskId"] = taskId,
            ["SessionId"] = "qq:group:1",
            ["ScheduledAtUtc"] = TestClock.Start.UtcDateTime,
            ["Status"] = (int)ClockRunStatus.Succeeded,
        });
        await meta.InsertAsync(new MetaRecord { Id = "persistence-schema-version", Value = "1" });

        await _store.EnsureInitializedAsync();

        // 迁移后归属 agent、meta 升到 2
        var loaded = (await _store.LoadAllAsync()).Single();
        Assert.Equal("agent", loaded.PluginId);
        Assert.Equal("legacy", loaded.Content);
        var version = await scope.GetCollection<MetaRecord>("meta").FindByIdAsync("persistence-schema-version");
        Assert.Equal("2", version!.Value);
    }

    private sealed class MetaRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
