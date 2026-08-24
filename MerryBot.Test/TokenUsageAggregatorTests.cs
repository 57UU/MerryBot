using DataService;

namespace MerryBot.Test;

/// <summary>
/// TokenUsageAggregator：ai_messages token 用量的分桶与会话聚合纯函数。
/// 归一化约定：InputTokens 含缓存命中，uncached = max(0, input - cached)。
/// </summary>
public sealed class TokenUsageAggregatorTests
{
    private static AiMessageEntry Entry(string sessionKey, string messageType, long time,
        int inputTokens = 0, int outputTokens = 0, int cachedTokens = 0)
        => new(0, sessionKey, messageType, string.Empty, time, inputTokens, outputTokens, cachedTokens);

    [Fact]
    public void Buckets_Align_To_Offset_And_Are_Sorted()
    {
        // 偏移 +8h（东八区）：86400 桶应从本地零点起算
        var offset = 8 * 3600L;
        // 本地 2026-01-01 00:00:00（= 2025-12-31T16:00:00Z）
        var localMidnight = 1735660800L;
        var entries = new[]
        {
            Entry("s1", "assistant", localMidnight + 1800, inputTokens: 100),
            Entry("s1", "assistant", localMidnight + 90000, inputTokens: 50), // 次日
        };

        var buckets = TokenUsageAggregator.AggregateBuckets(entries, 86400, offset);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(localMidnight, buckets[0].BucketStart);
        Assert.Equal(localMidnight + 86400, buckets[1].BucketStart);
    }

    [Fact]
    public void Uncached_Is_Input_Minus_Cached_Clamped_At_Zero()
    {
        var entries = new[]
        {
            Entry("s1", "assistant", 1000, inputTokens: 300, outputTokens: 40, cachedTokens: 250),
            // 异常数据：cached > input 时截断为 0，不出负数
            Entry("s1", "assistant", 1001, inputTokens: 10, outputTokens: 5, cachedTokens: 20),
        };

        var bucket = Assert.Single(TokenUsageAggregator.AggregateBuckets(entries, 3600));

        Assert.Equal(270, bucket.CachedTokens);
        Assert.Equal(50, bucket.UncachedInputTokens); // (300-250) + max(0, 10-20)
        Assert.Equal(45, bucket.OutputTokens);
        Assert.Equal(bucket.CachedTokens + bucket.UncachedInputTokens + bucket.OutputTokens, bucket.TotalTokens);
    }

    [Fact]
    public void Sessions_Group_By_Key_And_Sort_By_Total_Desc()
    {
        var entries = new[]
        {
            Entry("a", "assistant", 100, inputTokens: 100, outputTokens: 10),
            Entry("b", "assistant", 200, inputTokens: 500, outputTokens: 50, cachedTokens: 100),
            Entry("b", "assistant", 300, inputTokens: 10, outputTokens: 1),
            Entry("a", "tool", 150), // 非 assistant 行不带用量，聚合器不做角色过滤，由查询层负责
        };

        var sessions = TokenUsageAggregator.AggregateSessions(entries);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("b", sessions[0].SessionKey);
        // b: cached 100 + uncached (500-100)+10 + output 50+1
        Assert.Equal(561, sessions[0].TotalTokens);
        Assert.Equal(2, sessions[0].MessageCount);
        Assert.Equal(300, sessions[0].LastTime);
        Assert.Equal("a", sessions[1].SessionKey);
        Assert.Equal(110, sessions[1].TotalTokens);
    }

    [Fact]
    public void Empty_Range_Returns_Empty_Results()
    {
        Assert.Empty(TokenUsageAggregator.AggregateBuckets([], 3600));
        Assert.Empty(TokenUsageAggregator.AggregateSessions([]));
    }

    [Fact]
    public void NonPositive_BucketSeconds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenUsageAggregator.AggregateBuckets([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenUsageAggregator.AggregateBuckets([], -3600));
    }
}
