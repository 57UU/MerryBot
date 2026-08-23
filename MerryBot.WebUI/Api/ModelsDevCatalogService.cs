using BotPlugin;
using LlmBackend;
using Microsoft.Extensions.Logging;
using ModelsDev.Sdk;
using ModelsDev.Sdk.Models;
using System.Text.Json;

namespace MerryBot.WebUI.Api;

/// <summary>
/// WebUI 对 models.dev 目录的查询、刷新和本地缓存。
/// 缓存沿用机器人数据目录中的 models.dev-api.json，避免升级后重新下载。
/// </summary>
internal sealed class ModelsDevCatalogService
{
    private readonly string cachePath;
    private readonly ILogger logger;
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private ModelsDevClient? catalog;
    private DateTimeOffset? catalogUpdatedAtUtc;
    private string? catalogSource;
    private string? catalogRefreshError;

    public ModelsDevCatalogService(string cachePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        this.cachePath = cachePath;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<IReadOnlyList<LlmCatalogModelDto>> GetModelsAsync(
        string? query,
        string? providerId,
        CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        var text = query?.Trim();
        var requestedProvider = providerId?.Trim();
        return catalog!.GetAllProviders()
            .Where(provider => string.IsNullOrWhiteSpace(requestedProvider) || provider.Id == requestedProvider)
            .SelectMany(provider => provider.Models.Select(model => ToDto(provider, model.Key, model.Value)))
            .Where(model => string.IsNullOrWhiteSpace(text)
                || model.ProviderName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || model.ModelId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || model.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    public async Task<IReadOnlyList<LlmCatalogProviderDto>> GetProvidersAsync(string? query, CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        var text = query?.Trim();
        return catalog!.GetAllProviders()
            .Where(provider => string.IsNullOrWhiteSpace(text)
                || provider.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
                || provider.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(provider => new LlmCatalogProviderDto(provider.Id, provider.Name, provider.Api, provider.Models.Count))
            .ToList();
    }

    public async Task<LlmCatalogStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        return GetStatus();
    }

    public async Task<LlmCatalogStatusDto> RefreshAsync(CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: true, cancellationToken);
        return GetStatus();
    }

    public async Task<LlmProviderCatalogImportCommand> CreateImportCommandAsync(
        LlmCatalogImportRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        var providerId = RequireId(request.ProviderId, nameof(request.ProviderId));
        var modelId = RequireId(request.ModelId, nameof(request.ModelId));
        var provider = catalog!.GetProviderOrThrow(providerId);
        var model = catalog.GetModel(provider.Id, modelId)
            ?? throw new KeyNotFoundException($"models.dev 中未找到模型: {provider.Id}/{modelId}");

        return new LlmProviderCatalogImportCommand(
            provider.Id,
            provider.Name,
            model.Id,
            model.Name,
            provider.Api,
            model.Limit?.Context ?? 0,
            model.Limit?.Output ?? 0,
            ToCapabilities(model),
            catalogUpdatedAtUtc,
            request.BaseUrl,
            request.ApiFormat,
            request.ApiKey,
            request.Enabled,
            ToPluginReasoningOptions(model));
    }

    private async Task EnsureCatalogAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && catalog != null)
        {
            return;
        }

        await catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && catalog != null)
            {
                return;
            }

            if (!force && await TryLoadCacheAsync(cancellationToken))
            {
                return;
            }

            try
            {
                var nextCatalog = new ModelsDevClient();
                var json = await nextCatalog.DownloadAsync(cancellationToken);
                nextCatalog.LoadFromJson(json);
                catalog = nextCatalog;
                catalogUpdatedAtUtc = DateTimeOffset.UtcNow;
                catalogSource = "models.dev";
                catalogRefreshError = null;
                try
                {
                    await SaveCacheAsync(json, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    catalogRefreshError = $"目录已更新，但写入本地缓存失败：{ex.Message}";
                    logger.LogWarning(ex, "{Message}", catalogRefreshError);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                catalogRefreshError = ex.Message;
                if (catalog != null)
                {
                    logger.LogWarning(ex, "models.dev 刷新失败，继续使用本地目录缓存。");
                    return;
                }
                throw;
            }
        }
        finally
        {
            catalogLock.Release();
        }
    }

    private async Task<bool> TryLoadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
            var cachedCatalog = new ModelsDevClient();
            cachedCatalog.LoadFromJson(json);
            catalog = cachedCatalog;
            catalogUpdatedAtUtc = File.GetLastWriteTimeUtc(cachePath);
            catalogSource = "local-cache";
            catalogRefreshError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "无法读取 models.dev 本地目录缓存，将尝试联网加载。");
            return false;
        }
    }

    private async Task SaveCacheAsync(string json, CancellationToken cancellationToken)
    {
        var temporaryPath = cachePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private LlmCatalogStatusDto GetStatus()
        => new(catalogSource ?? "unknown", catalogUpdatedAtUtc, catalogRefreshError);

    private static LlmCatalogModelDto ToDto(Provider provider, string catalogModelId, ModelInfo model)
        => new(
            provider.Id,
            provider.Name,
            catalogModelId,
            model.Name,
            provider.Api,
            model.Limit?.Context ?? 0,
            model.Limit?.Output ?? 0,
            ToCapabilities(model).ToString(),
            model.ToolCall,
            model.Reasoning,
            ToReasoningOptions(model));

    private static LlmModelCapabilities ToCapabilities(ModelInfo model)
    {
        var capabilities = LlmModelCapabilities.Text;
        if (model.Modalities?.Input.Any(item => item.Equals("image", StringComparison.OrdinalIgnoreCase)) == true)
            capabilities |= LlmModelCapabilities.ImageInput;
        if (model.Attachment)
            capabilities |= LlmModelCapabilities.AttachmentInput;
        if (model.ToolCall)
            capabilities |= LlmModelCapabilities.ToolCalls;
        if (model.Reasoning)
            capabilities |= LlmModelCapabilities.Reasoning;
        if (model.StructuredOutput)
            capabilities |= LlmModelCapabilities.StructuredOutput;
        return capabilities;
    }

    private static IReadOnlyList<LlmReasoningOptionDto>? ToReasoningOptions(ModelInfo model)
    {
        if (model.ReasoningOptions == null || model.ReasoningOptions.Count == 0) return null;
        return model.ReasoningOptions
            .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Type))
            .Select(o => new LlmReasoningOptionDto(o.Type.Trim().ToLowerInvariant(), o.Values == null || o.Values.Count == 0 ? null : o.Values.Select(v => v.Trim().ToLowerInvariant()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static IReadOnlyList<LlmReasoningOption>? ToPluginReasoningOptions(ModelInfo model)
    {
        if (model.ReasoningOptions == null || model.ReasoningOptions.Count == 0) return null;
        return model.ReasoningOptions
            .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Type))
            .Select(o => new LlmReasoningOption(o.Type.Trim().ToLowerInvariant(), o.Values == null || o.Values.Count == 0 ? null : o.Values.Select(v => v.Trim().ToLowerInvariant()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("不能为空。", parameterName);
        }

        var result = value.Trim();
        if (result.Length > 200 || result.Any(char.IsControl))
        {
            throw new ArgumentException("标识符格式无效。", parameterName);
        }
        return result;
    }
}
