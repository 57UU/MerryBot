namespace MerryBot.WebUI;

/// <summary>与 LlmProviderPlugin 管理 API 对应的只读配置视图；不包含任何 Key 原文。</summary>
public sealed record LlmConfigSnapshot(string? DefaultModelId, IReadOnlyList<LlmProviderDto> Providers);
public sealed record LlmProviderDto(
    string Id,
    string Name,
    string BaseUrl,
    string ApiFormat,
    bool Enabled,
    string? CatalogProviderId,
    IReadOnlyList<LlmModelDto> Models,
    IReadOnlyList<LlmKeyDto> Keys);
public sealed record LlmModelDto(
    string Id,
    string ProviderId,
    string Name,
    string RemoteModelId,
    int ContextLength,
    int MaxOutputTokens,
    string Capabilities,
    bool Enabled,
    DateTimeOffset? CatalogUpdatedAtUtc,
    string? ReasoningEffort,
    bool EnablePromptCache);
public sealed record LlmKeyDto(string Id, string Name, string Fingerprint, int Priority, bool Enabled, DateTimeOffset UpdatedAtUtc);
public sealed record LlmCatalogModelDto(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string Name,
    string? SuggestedBaseUrl,
    int ContextLength,
    int MaxOutputTokens,
    string Capabilities,
    bool ToolCall,
    bool Reasoning);
public sealed record LlmCatalogProviderDto(string Id, string Name, string? SuggestedBaseUrl, int ModelCount);
public sealed record LlmCatalogStatusDto(string Source, DateTimeOffset? UpdatedAtUtc, string? RefreshError);

public sealed record LlmCatalogImportRequest(string ProviderId, string ModelId, string? BaseUrl, string? ApiFormat, string? ApiKey, bool? Enabled);
public sealed record LlmSaveKeyRequest(string ProviderId, string? Name, string Secret, int Priority, bool Enabled);
public sealed record LlmSaveProviderRequest(string Name, string BaseUrl, string? ApiFormat, bool Enabled);
public sealed record LlmSaveModelRequest(string ProviderId, string Name, string RemoteModelId, int ContextLength, int MaxOutputTokens, int Capabilities, bool Enabled, string? ReasoningEffort = null, bool EnablePromptCache = false);
