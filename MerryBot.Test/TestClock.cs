using Agent.Session;
using Microsoft.Extensions.Time.Testing;

namespace MerryBot.Test;

/// <summary>测试辅助：构造任务、推进假时钟直到条件满足。</summary>
internal static class TestClock
{
    /// <summary>默认测试起点：2026-08-15 12:00:00 UTC。</summary>
    public static readonly DateTimeOffset Start = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public static ClockTask MakeTask(
        string sessionId,
        string cron = "0 * * * *",
        DateTimeOffset? nextRun = null,
        bool runOnce = false,
        bool enabled = true,
        int timeoutSeconds = 600,
        string timezone = "UTC")
    {
        return new ClockTask
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            CronExpression = cron,
            TimeZoneId = timezone,
            Content = "remind",
            Trigger = new ClockTrigger { Type = "group", Id = "123" },
            RunOnce = runOnce,
            TimeoutSeconds = timeoutSeconds,
            Enabled = enabled,
            NextRunAtUtc = nextRun,
            CreatedAtUtc = Start,
            UpdatedAtUtc = Start,
        };
    }

    /// <summary>
    /// 逐步推进假时钟（每步 <paramref name="step"/>），直到 <paramref name="condition"/> 为真。
    /// 逐步推进可避免"调度器尚未注册等待"的时序竞态，保证推进一定会被调度器观察到。
    /// </summary>
    public static async Task AdvanceUntilAsync(
        FakeTimeProvider time,
        Func<bool> condition,
        TimeSpan? step = null,
        int maxSteps = 600)
    {
        var stepValue = step ?? TimeSpan.FromMinutes(1);
        for (var i = 0; i < maxSteps; i++)
        {
            if (condition())
            {
                return;
            }
            time.Advance(stepValue);
            await Task.Delay(20); // 让调度线程跑起来
        }
        Assert.Fail($"等待条件在 {maxSteps} 步内未满足");
    }

    /// <summary>同 <see cref="AdvanceUntilAsync(FakeTimeProvider, Func{bool}, TimeSpan?, int)"/>，条件为异步。</summary>
    public static async Task AdvanceUntilAsync(
        FakeTimeProvider time,
        Func<Task<bool>> condition,
        TimeSpan? step = null,
        int maxSteps = 600)
    {
        var stepValue = step ?? TimeSpan.FromMinutes(1);
        for (var i = 0; i < maxSteps; i++)
        {
            if (await condition())
            {
                return;
            }
            time.Advance(stepValue);
            await Task.Delay(20); // 让调度线程跑起来
        }
        Assert.Fail($"等待条件在 {maxSteps} 步内未满足");
    }
}
