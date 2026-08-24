namespace DataService;

/// <summary>
/// ai_messages token 用量的内存聚合（internal 纯函数，供单测直测）。
/// 归一化约定：InputTokens 含缓存命中（cached ⊆ input），uncached = max(0, input - cached)。
/// 只有 assistant 消息携带真实用量，调用方负责过滤。
/// </summary>
internal static class TokenUsageAggregator
{
    /// <summary>
    /// 把时间范围内的条目按时间桶聚合；返回按桶起始时间升序排列。
    /// bucketSeconds 为桶长度；offsetSeconds 为本地时区偏移（秒），使天级桶对齐本地零点而非 UTC 零点。
    /// </summary>
    public static List<TokenUsageBucket> AggregateBuckets(IEnumerable<AiMessageEntry> entries, long bucketSeconds, long offsetSeconds = 0)
    {
        if (bucketSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds), "桶长度必须为正数");
        }
        return entries
            .GroupBy(entry => FloorToBucket(entry.Time, bucketSeconds, offsetSeconds))
            .Select(group =>
            {
                long cached = 0, uncached = 0, output = 0;
                foreach (var entry in group)
                {
                    cached += entry.CachedTokens;
                    uncached += Math.Max(0, entry.InputTokens - entry.CachedTokens);
                    output += entry.OutputTokens;
                }
                return new TokenUsageBucket(group.Key, cached, uncached, output);
            })
            .OrderBy(bucket => bucket.BucketStart)
            .ToList();
    }

    private static long FloorToBucket(long time, long bucketSeconds, long offsetSeconds)
        => (time + offsetSeconds) / bucketSeconds * bucketSeconds - offsetSeconds;

    /// <summary>把时间范围内的条目按 SessionKey 聚合；返回按总量倒序排列。</summary>
    public static List<TokenSessionSummary> AggregateSessions(IEnumerable<AiMessageEntry> entries)
    {
        return entries
            .GroupBy(entry => entry.SessionKey, StringComparer.Ordinal)
            .Select(group =>
            {
                long cached = 0, uncached = 0, output = 0, lastTime = 0;
                int count = 0;
                foreach (var entry in group)
                {
                    cached += entry.CachedTokens;
                    uncached += Math.Max(0, entry.InputTokens - entry.CachedTokens);
                    output += entry.OutputTokens;
                    lastTime = Math.Max(lastTime, entry.Time);
                    count++;
                }
                return new TokenSessionSummary(group.Key, cached, uncached, output, count, lastTime);
            })
            .OrderByDescending(summary => summary.TotalTokens)
            .ToList();
    }
}
