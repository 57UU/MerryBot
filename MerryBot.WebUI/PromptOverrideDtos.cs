namespace MerryBot.WebUI;

public sealed record PromptOverrideSessionDto(
    string SessionKey,
    string DisplayName,
    DateTimeOffset UpdatedAtUtc);

public sealed record PromptOverrideDetailDto(
    string SessionKey,
    bool HasOverride,
    string Prompt,
    DateTimeOffset? UpdatedAtUtc);

public sealed record PromptOverrideSaveRequest(string SessionKey, string Prompt);
