using CommonLib;
using LiteDB.Async;

namespace DataService;

/// <summary>
/// AI 消息审计存储：ai_messages 集合的读写与 token 用量聚合。
/// 与 <see cref="HistoryRecorder"/> 共享同一 group_history.db（由其构造并持有数据库生命周期），
/// schema 迁移统一由 HistoryRecorder.MigrateAsync 负责，本类不触碰 UserVersion。
/// </summary>
public class AiMessageStore
{
    private readonly ILiteCollectionAsync<AiMessageEntry> aiMessagesCollection;
    private readonly IdGen.IdGenerator idGenerator;
    private readonly ISimpleLogger _logger;

    internal AiMessageStore(LiteDatabaseAsync database, IdGen.IdGenerator idGenerator, ISimpleLogger? logger = null)
    {
        this.idGenerator = idGenerator;
        _logger = logger ?? SimpleLog.Default;
        aiMessagesCollection = database.GetCollection<AiMessageEntry>("ai_messages");
        // 与 HistoryRecorder.EnsureIndexesAsync 同策略：失败只记日志不抛出
        try
        {
            aiMessagesCollection.EnsureIndexAsync(x => x.SessionKey).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[AiMessageStore] SessionKey 索引创建失败（查询性能可能下降）: {ex.GetBaseException().Message}");
        }
    }

    public async Task<bool> RecordAiMessageAsync(string sessionKey, string messageType, string content, int inputTokens = 0, int outputTokens = 0, int cachedTokens = 0)
    {
        var entry = new AiMessageEntry(idGenerator.CreateId(), sessionKey, messageType, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), inputTokens, outputTokens, cachedTokens);
        await aiMessagesCollection.InsertAsync(entry);
        return true;
    }

    /// <summary>
    /// 时间范围内 token 用量的时间桶聚合（只统计携带真实用量的 assistant 消息）。
    /// 桶按 offsetSeconds 偏移对齐（默认 UTC）；范围内的空桶不返回，由调用方（图表层）补零。
    /// </summary>
    public async Task<List<TokenUsageBucket>> GetTokenUsageBucketsAsync(long sinceUnixSec, long untilUnixSec, long bucketSeconds, long offsetSeconds = 0)
    {
        var entries = await QueryUsageEntriesAsync(sinceUnixSec, untilUnixSec);
        return TokenUsageAggregator.AggregateBuckets(entries, bucketSeconds, offsetSeconds);
    }

    /// <summary>时间范围内按会话聚合的 token 用量（只统计 assistant 消息），按总量倒序。</summary>
    public async Task<List<TokenSessionSummary>> GetTokenUsageBySessionAsync(long sinceUnixSec, long untilUnixSec)
    {
        var entries = await QueryUsageEntriesAsync(sinceUnixSec, untilUnixSec);
        return TokenUsageAggregator.AggregateSessions(entries);
    }

    private async Task<List<AiMessageEntry>> QueryUsageEntriesAsync(long sinceUnixSec, long untilUnixSec)
    {
        return await aiMessagesCollection.Query()
            .Where(entry => entry.Time >= sinceUnixSec && entry.Time < untilUnixSec && entry.MessageType == "assistant")
            .ToListAsync();
    }

    public async Task<List<AiMessageEntry>> GetAiMessagesBySessionKeyAsync(string sessionKey, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        var skip = (page - 1) * pageSize;
        return await aiMessagesCollection.Query()
            .Where(x => x.SessionKey == sessionKey)
            .OrderByDescending(x => x.Id)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetAiMessageCountBySessionKeyAsync(string sessionKey)
    {
        return await aiMessagesCollection.CountAsync(x => x.SessionKey == sessionKey);
    }
}
