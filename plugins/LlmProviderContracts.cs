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

/// <summary>本地模型配置的安全描述，不包含任何 API Key。</summary>
public sealed record LlmModelDescriptor(
    string Id,
    string ProviderId,
    string Name,
    string RemoteModelId,
    int ContextLength,
    int MaxOutputTokens,
    LlmModelCapabilities Capabilities,
    bool Enabled);

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
    Task<ResolvedLlmClient> CreateClientAsync(string? modelId = null, CancellationToken cancellationToken = default);
}
