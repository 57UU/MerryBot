using BotPlugin;
using Microsoft.Extensions.Time.Testing;

namespace MerryBot.Test;

/// <summary>
/// 自动水群单元测试：发送配额语义与旁观缓冲区的条数/超时触发。
/// 时间相关断言只用 FakeTimeProvider，不依赖真实时钟。
/// </summary>
public sealed class AutoChatTests
{
    // ── 配额 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Budget_Allows_Sends_Within_Limit()
    {
        AutoChatSendBudget budget = new();
        budget.BeginRound(2);
        try
        {
            Assert.True(budget.TryAcquire());
            Assert.True(budget.TryAcquire());
            Assert.False(budget.TryAcquire());
        }
        finally
        {
            budget.EndRound();
        }
    }

    [Fact]
    public void Budget_Allows_Unlimited_Sends_Outside_Round()
    {
        AutoChatSendBudget budget = new();
        Assert.True(budget.TryAcquire());
        Assert.True(budget.TryAcquire());
    }

    [Fact]
    public void Budget_Reset_On_Next_Round()
    {
        AutoChatSendBudget budget = new();
        budget.BeginRound(1);
        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());
        budget.EndRound();
        budget.BeginRound(1);
        try
        {
            Assert.True(budget.TryAcquire());
        }
        finally
        {
            budget.EndRound();
        }
    }

    // ── 配置默认值：实验功能默认关闭 ────────────────────────────────────────

    [Fact]
    public void AutoChat_Disabled_By_Default()
    {
        AgentConfig config = new();
        Assert.False(config.AutoChatEnable);
        Assert.Empty(config.AutoChatGroups);
        Assert.True(config.AutoChatDryRun);
        Assert.Equal(10, config.AutoChatBatchSize);
        Assert.Equal(60, config.AutoChatFlushSeconds);
        Assert.Equal(2, config.AutoChatMaxSendsPerTrigger);
    }

    // ── 缓冲区触发 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Buffer_Fires_When_Batch_Size_Reached()
    {
        FakeTimeProvider time = new(TestClock.Start);
        TaskCompletionSource<IReadOnlyList<AutoChatMessage>> flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AutoChatBuffer buffer = new(() => 3, () => TimeSpan.FromSeconds(60),
            batch =>
            {
                flushed.TrySetResult(batch);
                return Task.CompletedTask;
            }, time);

        buffer.Add(new AutoChatMessage(1, "a", "第一条"));
        buffer.Add(new AutoChatMessage(2, "b", "第二条"));
        Assert.False(flushed.Task.IsCompleted);
        buffer.Add(new AutoChatMessage(3, "c", "第三条"));

        IReadOnlyList<AutoChatMessage> batch = await WaitForAsync(flushed.Task);
        Assert.Equal(3, batch.Count);
        Assert.Equal("第一条", batch[0].Content);
    }

    [Fact]
    public async Task Buffer_Fires_On_Timeout_With_Partial_Batch()
    {
        FakeTimeProvider time = new(TestClock.Start);
        TaskCompletionSource<IReadOnlyList<AutoChatMessage>> flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AutoChatBuffer buffer = new(() => 10, () => TimeSpan.FromSeconds(60),
            batch =>
            {
                flushed.TrySetResult(batch);
                return Task.CompletedTask;
            }, time);

        buffer.Add(new AutoChatMessage(1, "a", "只有一条"));
        time.Advance(TimeSpan.FromSeconds(61));

        IReadOnlyList<AutoChatMessage> batch = await WaitForAsync(flushed.Task);
        Assert.Single(batch);
    }

    [Fact]
    public async Task Buffer_Empty_Timeout_Does_Not_Fire()
    {
        FakeTimeProvider time = new(TestClock.Start);
        int fireCount = 0;
        using AutoChatBuffer buffer = new(() => 10, () => TimeSpan.FromSeconds(60),
            batch =>
            {
                Interlocked.Increment(ref fireCount);
                return Task.CompletedTask;
            }, time);

        time.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(200);
        Assert.Equal(0, fireCount);
    }

    [Fact]
    public async Task Buffer_Dispose_Stops_Delivery()
    {
        FakeTimeProvider time = new(TestClock.Start);
        int fireCount = 0;
        AutoChatBuffer buffer = new(() => 1, () => TimeSpan.FromSeconds(60),
            batch =>
            {
                Interlocked.Increment(ref fireCount);
                return Task.CompletedTask;
            }, time);
        buffer.Dispose();

        buffer.Add(new AutoChatMessage(1, "a", "释放后再加"));
        time.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(200);
        Assert.Equal(0, fireCount);
    }

    private static async Task<IReadOnlyList<AutoChatMessage>> WaitForAsync(Task<IReadOnlyList<AutoChatMessage>> task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed);
        return await task;
    }
}
