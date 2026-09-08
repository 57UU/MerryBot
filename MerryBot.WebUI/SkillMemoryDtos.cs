namespace MerryBot.WebUI;

public sealed record SkillEnabledRequest(string Name, bool Enabled);
public sealed record SkillCloneRequest(string GitUrl, string? Name);

public sealed record MemorySessionDto(
    string SessionKey,
    string DisplayName,
    DateTimeOffset UpdatedAtUtc);

public sealed record MemoryIndexUpdateRequest(string SessionKey, string Content);
public sealed record MemoryEntryUpdateRequest(string SessionKey, string Key, string Content);

public sealed record ContextSessionDto(
    string SessionKey,
    string DisplayName,
    int MessageCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record SessionBusyDto(bool Busy);

public sealed record SessionClearRequest(string SessionKey);

public sealed record SessionClearResult(bool Cleared);
