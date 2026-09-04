using MerryBot.Contracts;
using DataProvider;
using LiteDB;
using LiteDB.Async;

namespace BotPlugin;

/// <summary>
/// Agent 的按群系统提示词复写存储。所有数据均以 SessionKey 分区，未复写的会话回退全局
/// <c>AgentConfig.AiPrompt</c>。与记忆服务共享同一数据库 scope（<c>agent</c>）。
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

    public async Task<IReadOnlyList<PromptOverrideSession>> ListOverridesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await overrides.FindAllAsync())
            .GroupBy(item => item.SessionKey, StringComparer.Ordinal)
            .Select(group => new PromptOverrideSession(
                group.Key,
                ToDateTimeOffset(group.Max(item => item.UpdatedAtUtc))))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    public async Task<PromptOverrideEntry?> GetOverrideAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        var record = await overrides.FindByIdAsync(normalizedSessionKey);
        return record is null
            ? null
            : new PromptOverrideEntry(record.SessionKey, record.Prompt, ToDateTimeOffset(record.UpdatedAtUtc));
    }

    public async Task SaveOverrideAsync(string sessionKey, string prompt, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        prompt = prompt.Trim();
        if (prompt.Length > IPromptOverrideService.MaxPromptLength)
        {
            throw new ArgumentException($"提示词过长（最多 {IPromptOverrideService.MaxPromptLength} 字符）。", nameof(prompt));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await overrides.FindByIdAsync(normalizedSessionKey);
        var now = DateTime.UtcNow;
        await overrides.UpsertAsync(new PromptOverrideRecord
        {
            Id = normalizedSessionKey,
            SessionKey = normalizedSessionKey,
            Prompt = prompt,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        });
    }

    public async Task<bool> DeleteOverrideAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        var normalizedSessionKey = ValidateSessionKey(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        return await overrides.DeleteAsync(normalizedSessionKey);
    }

    /// <summary>仅允许 qq 群会话复写：与 <c>AgentPlugin.CreateAgent</c> 支持的会话类型保持一致。</summary>
    private static string ValidateSessionKey(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var parsed = SessionKey.Parse(sessionKey);
        if (parsed.Platform != "qq" || parsed.ChannelType != "group")
        {
            throw new ArgumentException("仅支持 qq 群会话的提示词复写。", nameof(sessionKey));
        }
        return sessionKey;
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class PromptOverrideRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string SessionKey { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
