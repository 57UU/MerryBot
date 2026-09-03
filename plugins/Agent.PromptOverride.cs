using CommonLib;
using DataProvider;
using LiteDB;
using LiteDB.Async;

namespace BotPlugin;

/// <summary>
/// 按群提示词 override 存储：每个 sessionKey 最多一条记录，为空即回退全局提示词。
/// 与记忆服务共享同一数据库 scope，范式照抄（SessionKey 分区、确定性 Id、UTC 时间）。
/// </summary>
internal sealed class PromptOverrideService : IPromptOverrideService
{
    private readonly ILiteCollectionAsync<PromptOverrideRecord> overrides;

    public PromptOverrideService(PluginDatabaseScope database)
    {
        ArgumentNullException.ThrowIfNull(database);
        overrides = database.GetCollection<PromptOverrideRecord>("prompt_overrides");
        _ = overrides.EnsureIndexAsync(item => item.SessionKey);
        _ = overrides.EnsureIndexAsync(item => item.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<ManagedPromptOverride>> ListOverridesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await overrides.FindAllAsync())
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Select(static item => new ManagedPromptOverride(
                item.SessionKey,
                item.Content,
                ToDateTimeOffset(item.UpdatedAtUtc)))
            .ToList();
    }

    public async Task<string> GetOverrideAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        var record = await overrides.FindOneAsync(item => item.SessionKey == normalizedSessionKey);
        return record?.Content ?? string.Empty;
    }

    /// <summary>保存 override：空或全空白内容视为删除（回退全局提示词）。</summary>
    public async Task SaveOverrideAsync(string sessionKey, string content, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(content))
        {
            await overrides.DeleteManyAsync(item => item.SessionKey == normalizedSessionKey);
            return;
        }
        var existing = await overrides.FindOneAsync(item => item.SessionKey == normalizedSessionKey);
        var now = DateTime.UtcNow;
        await overrides.UpsertAsync(new PromptOverrideRecord
        {
            Id = normalizedSessionKey,
            SessionKey = normalizedSessionKey,
            Content = content.Trim(),
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        });
    }

    public async Task<bool> DeleteOverrideAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        return await overrides.DeleteManyAsync(item => item.SessionKey == normalizedSessionKey) > 0;
    }

    private static string ValidateSessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        _ = SessionKey.Parse(sessionKey);
        return sessionKey.Trim();
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class PromptOverrideRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string SessionKey { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
