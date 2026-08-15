namespace MerryBot.WebUI;

public sealed record SkillEnabledRequest(string Name, bool Enabled);

public sealed record MemorySessionDto(
    string SessionKey,
    string DisplayName,
    DateTimeOffset UpdatedAtUtc);

public sealed record MemoryIndexUpdateRequest(string SessionKey, string Content);
public sealed record MemoryEntryUpdateRequest(string SessionKey, string Key, string Content);
