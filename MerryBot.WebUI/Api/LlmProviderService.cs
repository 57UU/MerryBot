using BotPlugin;
using LlmBackend;

namespace MerryBot.WebUI.Api;

/// <summary>
/// LLM Provider 管理的进程内门面：Blazor 组件经 WebUiServiceRegistry 拿到它后直接调用，
/// 不再经 JS fetch 回调 /api/plugins/llm-provider。DTO 映射照抄 LlmProviderApiMapper，
/// 插件/目录服务为 null 时抛 InvalidOperationException（对等原来 API 404 的语义），由页面展示。
/// 无状态（只持有 registry 引用），组件可按需 new，不注册 DI（独立运行无插件时 registry 本就没有注册）。
/// </summary>
public sealed class LlmProviderService
{
    private readonly WebUiServiceRegistry registry;

    public LlmProviderService(WebUiServiceRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<LlmConfigSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => ToDto(await RequireManager().GetConfigurationAsync(cancellationToken));

    public Task SaveProviderAsync(string id, LlmSaveProviderRequest request, CancellationToken cancellationToken = default)
        => RequireManager().SaveProviderAsync(
            id,
            new LlmProviderSaveCommand(request.Name, request.BaseUrl, request.ApiFormat, request.Enabled),
            cancellationToken);

    public Task DeleteProviderAsync(string id, CancellationToken cancellationToken = default)
        => RequireManager().DeleteProviderAsync(id, cancellationToken);

    public Task SaveModelAsync(string id, LlmSaveModelRequest request, CancellationToken cancellationToken = default)
    {
        LlmModelSaveCommand command = new(
            request.ProviderId,
            request.RemoteModelId,
            request.ContextLength,
            request.MaxOutputTokens,
            (LlmModelCapabilities)request.Capabilities,
            request.Enabled,
            request.ReasoningEffort,
            request.EnablePromptCache,
            request.ReasoningOptions?.Select(o => new LlmReasoningOption(o.Type, o.Values)).ToList());
        return RequireManager().SaveModelAsync(id, command, cancellationToken);
    }

    public Task DeleteModelAsync(string id, CancellationToken cancellationToken = default)
        => RequireManager().DeleteModelAsync(id, cancellationToken);

    public Task SaveKeyAsync(LlmSaveKeyRequest request, CancellationToken cancellationToken = default)
        => RequireManager().SaveKeyAsync(
            new LlmProviderKeySaveCommand(request.ProviderId, request.Secret, request.Enabled),
            cancellationToken);

    public Task DeleteKeyAsync(string id, CancellationToken cancellationToken = default)
        => RequireManager().DeleteKeyAsync(id, cancellationToken);

    public Task SetDefaultModelAsync(string modelId, CancellationToken cancellationToken = default)
        => RequireManager().SetDefaultModelAsync(modelId, cancellationToken);

    public Task<IReadOnlyList<LlmCatalogProviderDto>> GetCatalogProvidersAsync(string? query, CancellationToken cancellationToken = default)
        => RequireCatalog().GetProvidersAsync(query, cancellationToken);

    public Task<IReadOnlyList<LlmCatalogModelDto>> GetCatalogModelsAsync(string? query, string? providerId, CancellationToken cancellationToken = default)
        => RequireCatalog().GetModelsAsync(query, providerId, cancellationToken);

    public Task<LlmCatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default)
        => RequireCatalog().GetStatusAsync(cancellationToken);

    public Task<LlmCatalogStatusDto> RefreshCatalogAsync(CancellationToken cancellationToken = default)
        => RequireCatalog().RefreshAsync(cancellationToken);

    public async Task<LlmConfigSnapshot> ImportCatalogModelAsync(LlmCatalogImportRequest request, CancellationToken cancellationToken = default)
    {
        LlmProviderCatalogImportCommand command = await RequireCatalog().CreateImportCommandAsync(request, cancellationToken);
        return ToDto(await RequireManager().ImportCatalogModelAsync(command, cancellationToken));
    }

    private ILlmProviderManagementService RequireManager()
        => registry.LlmProviders ?? throw new InvalidOperationException("LLM Provider 服务未加载（llm-provider 插件未启用）。");

    private ModelsDevCatalogService RequireCatalog()
        => registry.Catalog ?? throw new InvalidOperationException("模型目录服务不可用。");

    private static LlmConfigSnapshot ToDto(LlmProviderConfiguration source)
        => new(source.DefaultModelId, source.Providers.Select(ToDto).ToList());

    private static LlmProviderDto ToDto(LlmProviderConfigurationProvider source)
        => new(
            source.Id,
            source.Name,
            source.BaseUrl,
            ToApiFormatName(source.ApiFormat),
            source.Enabled,
            source.CatalogProviderId,
            source.Models.Select(ToDto).ToList(),
            source.Keys.Select(ToDto).ToList());

    private static LlmModelDto ToDto(LlmProviderConfigurationModel source)
        => new(
            source.Id,
            source.ProviderId,
            source.RemoteModelId,
            source.ContextLength,
            source.MaxOutputTokens,
            source.Capabilities.ToString(),
            source.Enabled,
            source.CatalogUpdatedAtUtc,
            source.ReasoningEffort,
            source.EnablePromptCache,
            source.ReasoningOptions?.Select(o => new LlmReasoningOptionDto(o.Type, o.Values)).ToList());

    private static LlmKeyDto ToDto(LlmProviderConfigurationKey source)
        => new(source.Id, source.Fingerprint, source.Enabled, source.UpdatedAtUtc);

    private static string ToApiFormatName(LlmApiFormat format)
        => format switch
        {
            LlmApiFormat.OpenAiChatCompletions => "openai-chat-completions",
            LlmApiFormat.OpenAiResponses => "openai-responses",
            LlmApiFormat.AnthropicMessages => "anthropic-messages",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
}
