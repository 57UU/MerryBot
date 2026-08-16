using BotPlugin;
using LlmBackend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>
/// 将 LLM Provider 管理能力暴露为 WebUI API。
/// 路径和响应结构保持与旧插件内嵌 API 兼容。
/// </summary>
public static class LlmProviderApiMapper
{
    public static void Map(WebApplication app, ILlmProviderManagementService manager, string botPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(botPathPrefix);

        var catalog = new ModelsDevCatalogService(Path.Combine(botPathPrefix, "models.dev-api.json"), app.Logger);

        var routes = app.MapGroup("/api/plugins/llm-provider");
        routes.MapGet("/config", async (CancellationToken cancellationToken) =>
            Results.Ok(ToDto(await manager.GetConfigurationAsync(cancellationToken))));
        routes.MapGet("/catalog", async (string? query, string? providerId, CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetModelsAsync(query, providerId, cancellationToken)));
        routes.MapGet("/catalog/providers", async (string? query, CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetProvidersAsync(query, cancellationToken)));
        routes.MapGet("/catalog/status", async (CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetStatusAsync(cancellationToken)));
        routes.MapPost("/catalog/refresh", async (CancellationToken cancellationToken) =>
            Results.Ok(await catalog.RefreshAsync(cancellationToken)));
        routes.MapPost("/import", async (LlmCatalogImportRequest request, CancellationToken cancellationToken) =>
            Results.Ok(ToDto(await manager.ImportCatalogModelAsync(
                await catalog.CreateImportCommandAsync(request, cancellationToken), cancellationToken))));
        routes.MapPost("/providers/{id}", async (string id, LlmSaveProviderRequest request, CancellationToken cancellationToken) =>
        {
            await manager.SaveProviderAsync(id, new LlmProviderSaveCommand(request.Name, request.BaseUrl, request.ApiFormat, request.Enabled), cancellationToken);
            return Results.NoContent();
        });
        routes.MapPost("/providers/{id}/delete", async (string id, CancellationToken cancellationToken) =>
        {
            await manager.DeleteProviderAsync(id, cancellationToken);
            return Results.NoContent();
        });
        routes.MapPost("/models/{**id}", async (string id, LlmSaveModelRequest request, CancellationToken cancellationToken) =>
        {
            // catch-all 参数不解码百分号编码，手动还原 %2F → /（否则含 / 的模型 ID 会以字面 %2F 落库）
            var modelId = Uri.UnescapeDataString(id);
            await manager.SaveModelAsync(modelId, new LlmModelSaveCommand(
                request.ProviderId,
                request.Name,
                request.RemoteModelId,
                request.ContextLength,
                request.MaxOutputTokens,
                (LlmModelCapabilities)request.Capabilities,
                request.Enabled,
                request.ReasoningEffort,
                request.EnablePromptCache), cancellationToken);
            return Results.NoContent();
        });
        routes.MapPost("/models/{**id}/delete", async (string id, CancellationToken cancellationToken) =>
        {
            await manager.DeleteModelAsync(Uri.UnescapeDataString(id), cancellationToken);
            return Results.NoContent();
        });
        routes.MapPost("/keys", async (LlmSaveKeyRequest request, CancellationToken cancellationToken) =>
            Results.Ok(ToDto(await manager.SaveKeyAsync(
                new LlmProviderKeySaveCommand(request.ProviderId, request.Name, request.Secret, request.Priority, request.Enabled),
                cancellationToken))));
        routes.MapPost("/keys/{id}/delete", async (string id, CancellationToken cancellationToken) =>
        {
            await manager.DeleteKeyAsync(id, cancellationToken);
            return Results.NoContent();
        });
        routes.MapPost("/default/{**modelId}", async (string modelId, CancellationToken cancellationToken) =>
        {
            await manager.SetDefaultModelAsync(Uri.UnescapeDataString(modelId), cancellationToken);
            return Results.NoContent();
        });
    }

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
            source.Name,
            source.RemoteModelId,
            source.ContextLength,
            source.MaxOutputTokens,
            source.Capabilities.ToString(),
            source.Enabled,
            source.CatalogUpdatedAtUtc,
            source.ReasoningEffort,
            source.EnablePromptCache);

    private static LlmKeyDto ToDto(LlmProviderConfigurationKey source)
        => new(source.Id, source.Name, source.Fingerprint, source.Priority, source.Enabled, source.UpdatedAtUtc);

    private static string ToApiFormatName(LlmApiFormat format)
        => format switch
        {
            LlmApiFormat.OpenAiChatCompletions => "openai-chat-completions",
            LlmApiFormat.OpenAiResponses => "openai-responses",
            LlmApiFormat.AnthropicMessages => "anthropic-messages",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
}
