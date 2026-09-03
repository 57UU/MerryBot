using LlmBackend;
using LlmClient;

namespace BotPlugin;

/// <summary>当前可执行的上游 API 格式。</summary>
public enum LlmApiFormat
{
    OpenAiChatCompletions,
    OpenAiResponses,
    AnthropicMessages,
}

public sealed record LlmReasoningOption(string Type, IReadOnlyList<string>? Values);

/// <summary>本地模型配置的安全描述，不包含任何 API Key。</summary>
public sealed record LlmModelDescriptor(
    string Id,
    string ProviderId,
    string Name,
    string RemoteModelId,
    int ContextLength,
    int MaxOutputTokens,
    LlmModelCapabilities Capabilities,
    bool Enabled,
    string? ReasoningEffort = null,
    IReadOnlyList<LlmReasoningOption>? ReasoningOptions = null);

/// <summary>由 Provider 与模型配置解析出的可直接使用客户端。</summary>
public sealed record ResolvedLlmClient(
    LlmModelDescriptor Model,
    Client Client);

/// <summary>
/// 供 Agent 与其他插件使用的 LLM 注册表。配置、模型和 Key 均由 LlmProviderPlugin 管理。
/// </summary>
public interface ILlmProviderRegistry
{
    Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<LlmModelDescriptor> GetModelAsync(string modelId, CancellationToken cancellationToken = default);
    /// <summary>
    /// 按模型 Id 解析出可直接使用的客户端。sessionKey 为 OpenCode 会话亲和 key（可选，
    /// 传入则后端原样使用，未传入则后端实例自动维护一个稳定随机数；非 OpenCode 目标不发送）。
    /// </summary>
    Task<ResolvedLlmClient> CreateClientAsync(string? modelId = null, CancellationToken cancellationToken = default, string? sessionKey = null);
}

/// <summary>
/// LLM Provider 的管理能力。由 WebUI 等管理入口使用，不包含任何 ASP.NET 类型。
/// </summary>
public interface ILlmProviderManagementService
{
    Task<LlmProviderConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<LlmProviderConfiguration> ImportCatalogModelAsync(LlmProviderCatalogImportCommand command, CancellationToken cancellationToken = default);
    Task SaveProviderAsync(string id, LlmProviderSaveCommand command, CancellationToken cancellationToken = default);
    Task DeleteProviderAsync(string id, CancellationToken cancellationToken = default);
    Task SaveModelAsync(string id, LlmModelSaveCommand command, CancellationToken cancellationToken = default);
    Task DeleteModelAsync(string id, CancellationToken cancellationToken = default);
    Task<LlmProviderConfigurationKey> SaveKeyAsync(LlmProviderKeySaveCommand command, CancellationToken cancellationToken = default);
    Task DeleteKeyAsync(string id, CancellationToken cancellationToken = default);
    Task SetDefaultModelAsync(string modelId, CancellationToken cancellationToken = default);
}

/// <summary>可安全展示的本地 Provider 配置；不会包含 API Key 明文。</summary>
public sealed record LlmProviderConfiguration(
    string? DefaultModelId,
    IReadOnlyList<LlmProviderConfigurationProvider> Providers);

public sealed record LlmProviderConfigurationProvider(
    string Id,
    string Name,
    string BaseUrl,
    LlmApiFormat ApiFormat,
    bool Enabled,
    string? CatalogProviderId,
    IReadOnlyList<LlmProviderConfigurationModel> Models,
    IReadOnlyList<LlmProviderConfigurationKey> Keys);

public sealed record LlmProviderConfigurationModel(
    string Id,
    string ProviderId,
    string Name,
    string RemoteModelId,
    int ContextLength,
    int MaxOutputTokens,
    LlmModelCapabilities Capabilities,
    bool Enabled,
    DateTimeOffset? CatalogUpdatedAtUtc,
    string? ReasoningEffort,
    bool EnablePromptCache,
    IReadOnlyList<LlmReasoningOption>? ReasoningOptions = null);

public sealed record LlmProviderConfigurationKey(
    string Id,
    string Name,
    string Fingerprint,
    int Priority,
    bool Enabled,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 由 WebUI 在查询外部模型目录后生成的导入数据。
/// 插件只持久化这些已解析的本地配置，不负责目录查询、刷新或缓存。
/// </summary>
public sealed record LlmProviderCatalogImportCommand(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ModelName,
    string? SuggestedBaseUrl,
    int ContextLength,
    int MaxOutputTokens,
    LlmModelCapabilities Capabilities,
    DateTimeOffset? CatalogUpdatedAtUtc,
    string? BaseUrl,
    string? ApiFormat,
    string? ApiKey,
    bool? Enabled,
    IReadOnlyList<LlmReasoningOption>? ReasoningOptions = null);

public sealed record LlmProviderSaveCommand(string Name, string BaseUrl, string? ApiFormat, bool Enabled);

public sealed record LlmModelSaveCommand(
    string ProviderId,
    string Name,
    string RemoteModelId,
    int ContextLength,
    int MaxOutputTokens,
    LlmModelCapabilities Capabilities,
    bool Enabled,
    string? ReasoningEffort = null,
    bool EnablePromptCache = false,
    IReadOnlyList<LlmReasoningOption>? ReasoningOptions = null);

public sealed record LlmProviderKeySaveCommand(string ProviderId, string? Name, string Secret, int Priority, bool Enabled);
