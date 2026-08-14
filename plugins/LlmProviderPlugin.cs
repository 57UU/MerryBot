using LiteDB;
using LiteDB.Async;
using LlmBackend;
using LlmClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelsDev.Sdk;
using ModelsDev.Sdk.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BotPlugin;

/// <summary>
/// 管理可执行 LLM Provider、模型和 API Key。models.dev 仅作为目录元数据来源；
/// 导入后仍由本地配置决定接口地址、格式和是否启用。
/// </summary>
[PluginTag("llm-provider", "LLM Provider", "管理 LLM Provider、模型、Key 与 models.dev 导入")]
public sealed class LlmProviderPlugin : Plugin, ILlmProviderRegistry
{
    private const string DefaultModelMetaId = "default-model";
    private const string SchemaVersionMetaId = "schema-version";
    private const string SchemaVersion = "1";
    private const string CatalogCacheFileName = "models.dev-api.json";
    private readonly ILiteCollectionAsync<ProviderRecord> providers;
    private readonly ILiteCollectionAsync<ModelRecord> models;
    private readonly ILiteCollectionAsync<KeyRecord> keys;
    private readonly ILiteCollectionAsync<MetaRecord> meta;
    private readonly IDataProtector keyProtector;
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private readonly string catalogCachePath;
    private ModelsDevClient? catalog;
    private DateTimeOffset? catalogUpdatedAtUtc;
    private string? catalogSource;
    private string? catalogRefreshError;

    public LlmProviderPlugin(PluginInterop interop) : base(interop)
    {
        providers = interop.PluginDatabase.GetCollection<ProviderRecord>("providers");
        models = interop.PluginDatabase.GetCollection<ModelRecord>("models");
        keys = interop.PluginDatabase.GetCollection<KeyRecord>("keys");
        meta = interop.PluginDatabase.GetCollection<MetaRecord>("meta");

        Directory.CreateDirectory(interop.PathPrefix);
        var keyRingPath = Path.Combine(interop.PathPrefix, "llm-provider-key-ring");
        Directory.CreateDirectory(keyRingPath);
        catalogCachePath = Path.Combine(interop.PathPrefix, CatalogCacheFileName);
        keyProtector = DataProtectionProvider
            .Create(new DirectoryInfo(keyRingPath), builder => builder.SetApplicationName("MerryBot.LlmProvider"))
            .CreateProtector("api-key.v1");

        MapApiRoutes();
        _ = EnsureIndexesAsync();
    }

    public override async Task OnLoaded()
    {
        await EnsureIndexesAsync();
        Logger.Info("llm provider plugin start");
    }

    public async Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (await models.FindAllAsync())
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(ToDescriptor)
            .ToList();
        return result;
    }

    public async Task<LlmModelDescriptor> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = await models.FindByIdAsync(RequireId(modelId, nameof(modelId)));
        return model == null
            ? throw new KeyNotFoundException($"未找到模型: {modelId}")
            : ToDescriptor(model);
    }

    public async Task<ResolvedLlmClient> CreateClientAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        modelId ??= await GetDefaultModelIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new PluginNotUsableException("尚未配置默认 LLM 模型，请在 LLM Provider 页面导入模型。");
        }

        var model = await models.FindByIdAsync(modelId)
            ?? throw new KeyNotFoundException($"未找到模型: {modelId}");
        if (!model.Enabled)
        {
            throw new InvalidOperationException($"模型已禁用: {model.Id}");
        }

        var provider = await providers.FindByIdAsync(model.ProviderId)
            ?? throw new InvalidOperationException($"模型 {model.Id} 的 Provider 不存在: {model.ProviderId}");
        if (!provider.Enabled)
        {
            throw new InvalidOperationException($"Provider 已禁用: {provider.Id}");
        }
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new InvalidOperationException($"Provider {provider.Id} 未设置 API 地址。");
        }

        var key = (await keys.FindAllAsync())
            .Where(item => item.ProviderId == provider.Id && item.Enabled)
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new PluginNotUsableException($"Provider {provider.Id} 没有可用 API Key。");
        var apiKey = keyProtector.Unprotect(key.ProtectedSecret);
        Backend backend = provider.ApiFormat switch
        {
            LlmApiFormat.OpenAiChatCompletions => new ChatCompletionBackend(provider.BaseUrl, apiKey, model.RemoteModelId),
            LlmApiFormat.OpenAiResponses => new ResponsesBackend(provider.BaseUrl, apiKey, model.RemoteModelId),
            LlmApiFormat.AnthropicMessages => new AnthropicBackend(provider.BaseUrl, apiKey, model.RemoteModelId, model.MaxOutputTokens),
            _ => throw new NotSupportedException($"不支持的 API 格式: {provider.ApiFormat}"),
        };
        var client = new Client(backend, new ClientConfig(3, TimeSpan.FromSeconds(1)));
        return new ResolvedLlmClient(ToDescriptor(model), client);
    }

    private async Task EnsureIndexesAsync()
    {
        await providers.EnsureIndexAsync(item => item.Id);
        await models.EnsureIndexAsync(item => item.ProviderId);
        await keys.EnsureIndexAsync(item => item.ProviderId);
        var schema = await meta.FindByIdAsync(SchemaVersionMetaId);
        if (schema == null)
        {
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionMetaId, Value = SchemaVersion });
        }
        else if (schema.Value != SchemaVersion)
        {
            throw new InvalidOperationException($"llm-provider 数据库版本不受支持: {schema.Value}");
        }
    }

    private void MapApiRoutes()
    {
        var routes = Interop.WebApplication.MapGroup("/api/plugins/llm-provider");
        routes.MapGet("/config", async (CancellationToken cancellationToken) =>
            Results.Ok(await GetConfigAsync(cancellationToken)));
        routes.MapGet("/catalog", async (string? query, string? providerId, CancellationToken cancellationToken) =>
            Results.Ok(await GetCatalogAsync(query, providerId, refresh: false, cancellationToken: cancellationToken)));
        routes.MapGet("/catalog/providers", async (string? query, CancellationToken cancellationToken) =>
            Results.Ok(await GetCatalogProvidersAsync(query, cancellationToken)));
        routes.MapGet("/catalog/status", async (CancellationToken cancellationToken) =>
        {
            await EnsureCatalogAsync(force: false, cancellationToken);
            return Results.Ok(GetCatalogStatus());
        });
        routes.MapPost("/catalog/refresh", async (CancellationToken cancellationToken) =>
        {
            await EnsureCatalogAsync(force: true, cancellationToken);
            return Results.Ok(GetCatalogStatus());
        });
        routes.MapPost("/import", async (CatalogImportRequest request, CancellationToken cancellationToken) =>
            Results.Ok(await ImportFromCatalogAsync(request, cancellationToken)));
        routes.MapPut("/providers/{id}", async (string id, SaveProviderRequest request, CancellationToken cancellationToken) =>
        {
            await SaveProviderAsync(id, request, cancellationToken);
            return Results.NoContent();
        });
        routes.MapDelete("/providers/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            await DeleteProviderAsync(id, cancellationToken);
            return Results.NoContent();
        });
        routes.MapPut("/models/{**id}", async (string id, SaveModelRequest request, CancellationToken cancellationToken) =>
        {
            await SaveModelAsync(id, request, cancellationToken);
            return Results.NoContent();
        });
        routes.MapDelete("/models/{**id}", async (string id, CancellationToken cancellationToken) =>
        {
            await models.DeleteAsync(id);
            await ClearDefaultModelIfAsync(id);
            return Results.NoContent();
        });
        routes.MapPost("/keys", async (SaveKeyRequest request, CancellationToken cancellationToken) =>
            Results.Ok(await SaveKeyAsync(request, cancellationToken)));
        routes.MapDelete("/keys/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            await keys.DeleteAsync(id);
            return Results.NoContent();
        });
        routes.MapPut("/default/{**modelId}", async (string modelId, CancellationToken cancellationToken) =>
        {
            var model = await models.FindByIdAsync(modelId)
                ?? throw new KeyNotFoundException($"未找到模型: {modelId}");
            await meta.UpsertAsync(new MetaRecord { Id = DefaultModelMetaId, Value = model.Id });
            return Results.NoContent();
        });
    }

    private async Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allProviders = await providers.FindAllAsync();
        var allModels = await models.FindAllAsync();
        var allKeys = await keys.FindAllAsync();
        var defaultModelId = await GetDefaultModelIdAsync(cancellationToken);
        var providerDtos = allProviders
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new ProviderDto(
                provider.Id,
                provider.Name,
                provider.BaseUrl,
                ToApiFormatName(provider.ApiFormat),
                provider.Enabled,
                provider.CatalogProviderId,
                allModels.Where(model => model.ProviderId == provider.Id)
                    .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(model => new ModelDto(
                        model.Id,
                        model.ProviderId,
                        model.Name,
                        model.RemoteModelId,
                        model.ContextLength,
                        model.MaxOutputTokens,
                        model.Capabilities.ToString(),
                        model.Enabled,
                        model.CatalogUpdatedAtUtc,
                        model.ReasoningEffort))
                    .ToList(),
                allKeys.Where(key => key.ProviderId == provider.Id)
                    .OrderBy(key => key.Priority)
                    .Select(key => new KeyDto(key.Id, key.Name, key.Fingerprint, key.Priority, key.Enabled, key.UpdatedAtUtc))
                    .ToList()))
            .ToList();
        return new ConfigSnapshot(defaultModelId, providerDtos);
    }

    private async Task<IReadOnlyList<CatalogModelDto>> GetCatalogAsync(
        string? query,
        string? providerId,
        bool refresh,
        CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(refresh, cancellationToken);
        var text = query?.Trim();
        var requestedProvider = providerId?.Trim();
        return catalog!.GetAllProviders()
            .Where(provider => string.IsNullOrWhiteSpace(requestedProvider) || provider.Id == requestedProvider)
            .SelectMany(provider => provider.Models.Select(model => ToCatalogDto(provider, model.Key, model.Value)))
            .Where(model => string.IsNullOrWhiteSpace(text)
                || model.ProviderName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || model.ModelId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || model.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }

    private async Task<IReadOnlyList<CatalogProviderDto>> GetCatalogProvidersAsync(string? query, CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        var text = query?.Trim();
        return catalog!.GetAllProviders()
            .Where(provider => string.IsNullOrWhiteSpace(text)
                || provider.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
                || provider.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(provider => new CatalogProviderDto(provider.Id, provider.Name, provider.Api, provider.Models.Count))
            .ToList();
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

            if (!force && await TryLoadCatalogCacheAsync(cancellationToken))
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
                    await SaveCatalogCacheAsync(json, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    catalogRefreshError = $"目录已更新，但写入本地缓存失败：{ex.Message}";
                    Logger.Warn(catalogRefreshError);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                catalogRefreshError = ex.Message;
                if (catalog != null)
                {
                    Logger.Warn($"models.dev 刷新失败，继续使用本地目录缓存：{ex.Message}");
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

    private async Task<bool> TryLoadCatalogCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(catalogCachePath))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(catalogCachePath, cancellationToken);
            var cachedCatalog = new ModelsDevClient();
            cachedCatalog.LoadFromJson(json);
            catalog = cachedCatalog;
            catalogUpdatedAtUtc = File.GetLastWriteTimeUtc(catalogCachePath);
            catalogSource = "local-cache";
            catalogRefreshError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"无法读取 models.dev 本地目录缓存，将尝试联网加载：{ex.Message}");
            return false;
        }
    }

    private async Task SaveCatalogCacheAsync(string json, CancellationToken cancellationToken)
    {
        var temporaryPath = catalogCachePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, catalogCachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private CatalogStatusDto GetCatalogStatus()
        => new(catalogSource ?? "unknown", catalogUpdatedAtUtc, catalogRefreshError);

    private async Task<ConfigSnapshot> ImportFromCatalogAsync(CatalogImportRequest request, CancellationToken cancellationToken)
    {
        await EnsureCatalogAsync(force: false, cancellationToken);
        var catalogProvider = catalog!.GetProviderOrThrow(RequireId(request.ProviderId, nameof(request.ProviderId)));
        var catalogModel = catalog.GetModel(catalogProvider.Id, RequireId(request.ModelId, nameof(request.ModelId)))
            ?? throw new KeyNotFoundException($"models.dev 中未找到模型: {catalogProvider.Id}/{request.ModelId}");

        var now = DateTimeOffset.UtcNow;
        var provider = await providers.FindByIdAsync(catalogProvider.Id);
        if (provider == null)
        {
            provider = new ProviderRecord
            {
                Id = catalogProvider.Id,
                Name = catalogProvider.Name,
                BaseUrl = NormalizeUrl(request.BaseUrl) ?? NormalizeUrl(catalogProvider.Api) ?? string.Empty,
                ApiFormat = ParseApiFormat(request.ApiFormat),
                CatalogProviderId = catalogProvider.Id,
                Enabled = request.Enabled ?? true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
        else
        {
            // models.dev 刷新只更新目录标识和显示名称，不覆盖用户手工填写的地址/格式/启用状态。
            provider.Name = catalogProvider.Name;
            provider.CatalogProviderId = catalogProvider.Id;
            provider.UpdatedAtUtc = now;
        }
        await providers.UpsertAsync(provider);

        var localModelId = MakeLocalModelId(catalogProvider.Id, catalogModel.Id);
        var model = await models.FindByIdAsync(localModelId) ?? new ModelRecord
        {
            Id = localModelId,
            ProviderId = provider.Id,
            CreatedAtUtc = now,
        };
        model.Name = catalogModel.Name;
        model.RemoteModelId = catalogModel.Id;
        model.ContextLength = catalogModel.Limit?.Context > 0 ? catalogModel.Limit.Context : 32_768;
        model.MaxOutputTokens = catalogModel.Limit?.Output > 0 ? catalogModel.Limit.Output : 4_096;
        model.Capabilities = ToCapabilities(catalogModel);
        model.CatalogProviderId = catalogProvider.Id;
        model.CatalogModelId = catalogModel.Id;
        model.CatalogUpdatedAtUtc = catalogUpdatedAtUtc;
        model.Enabled = request.Enabled ?? model.Enabled || model.CreatedAtUtc == now;
        model.UpdatedAtUtc = now;
        await models.UpsertAsync(model);

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            await SaveKeyAsync(new SaveKeyRequest(provider.Id, "默认 Key", request.ApiKey, 0, true), cancellationToken);
        }
        return await GetConfigAsync(cancellationToken);
    }

    private async Task SaveProviderAsync(string id, SaveProviderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = RequireId(id, nameof(id));
        var now = DateTimeOffset.UtcNow;
        var provider = await providers.FindByIdAsync(providerId) ?? new ProviderRecord
        {
            Id = providerId,
            CreatedAtUtc = now,
        };
        provider.Name = RequireText(request.Name, nameof(request.Name));
        provider.BaseUrl = NormalizeUrl(request.BaseUrl) ?? throw new ArgumentException("API 地址不能为空", nameof(request.BaseUrl));
        provider.ApiFormat = ParseApiFormat(request.ApiFormat);
        provider.Enabled = request.Enabled;
        provider.UpdatedAtUtc = now;
        await providers.UpsertAsync(provider);
    }

    private async Task SaveModelAsync(string id, SaveModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelId = RequireId(id, nameof(id));
        var providerId = RequireId(request.ProviderId, nameof(request.ProviderId));
        if (await providers.FindByIdAsync(providerId) == null)
        {
            throw new KeyNotFoundException($"未找到 Provider: {providerId}");
        }
        if (request.ContextLength < 1 || request.MaxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "上下文长度和最大输出必须为正数。");
        }
        var now = DateTimeOffset.UtcNow;
        var model = await models.FindByIdAsync(modelId) ?? new ModelRecord
        {
            Id = modelId,
            CreatedAtUtc = now,
        };
        model.ProviderId = providerId;
        model.Name = RequireText(request.Name, nameof(request.Name));
        model.RemoteModelId = RequireText(request.RemoteModelId, nameof(request.RemoteModelId));
        model.ContextLength = request.ContextLength;
        model.MaxOutputTokens = request.MaxOutputTokens;
        model.Capabilities = request.Capabilities;
        model.ReasoningEffort = NormalizeReasoningEffort(request.ReasoningEffort);
        model.Enabled = request.Enabled;
        model.UpdatedAtUtc = now;
        await models.UpsertAsync(model);
    }

    private async Task<KeyDto> SaveKeyAsync(SaveKeyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = RequireId(request.ProviderId, nameof(request.ProviderId));
        if (await providers.FindByIdAsync(providerId) == null)
        {
            throw new KeyNotFoundException($"未找到 Provider: {providerId}");
        }
        var secret = RequireText(request.Secret, nameof(request.Secret));
        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(request.Name) ? "API Key" : request.Name.Trim();
        var record = (await keys.FindAllAsync())
            .FirstOrDefault(item => item.ProviderId == providerId && item.Name == name)
            ?? new KeyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProviderId = providerId,
            CreatedAtUtc = now,
        };
        record.Name = name;
        record.ProtectedSecret = keyProtector.Protect(secret);
        record.Fingerprint = Fingerprint(secret);
        record.Priority = request.Priority;
        record.Enabled = request.Enabled;
        record.UpdatedAtUtc = now;
        await keys.UpsertAsync(record);
        return new KeyDto(record.Id, record.Name, record.Fingerprint, record.Priority, record.Enabled, record.UpdatedAtUtc);
    }

    private async Task DeleteProviderAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = RequireId(id, nameof(id));
        var affectedModelIds = (await models.FindAllAsync())
            .Where(item => item.ProviderId == providerId)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await providers.DeleteAsync(providerId);
        await models.DeleteManyAsync(item => item.ProviderId == providerId);
        await keys.DeleteManyAsync(item => item.ProviderId == providerId);
        var defaultModel = await GetDefaultModelIdAsync(cancellationToken);
        if (defaultModel != null && affectedModelIds.Contains(defaultModel))
        {
            await meta.DeleteAsync(DefaultModelMetaId);
        }
    }

    private async Task ClearDefaultModelIfAsync(string modelId)
    {
        var configured = await meta.FindByIdAsync(DefaultModelMetaId);
        if (string.Equals(configured?.Value, modelId, StringComparison.OrdinalIgnoreCase))
        {
            await meta.DeleteAsync(DefaultModelMetaId);
        }
    }

    private async Task<string?> GetDefaultModelIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = await meta.FindByIdAsync(DefaultModelMetaId);
        if (!string.IsNullOrWhiteSpace(configured?.Value))
        {
            return configured.Value;
        }
        return (await models.FindAllAsync())
            .Where(model => model.Enabled)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.Id)
            .FirstOrDefault();
    }

    private static CatalogModelDto ToCatalogDto(Provider provider, string catalogModelId, ModelInfo model)
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
            model.Reasoning);

    private static LlmModelDescriptor ToDescriptor(ModelRecord source)
        => new(source.Id, source.ProviderId, source.Name, source.RemoteModelId, source.ContextLength, source.MaxOutputTokens, source.Capabilities, source.Enabled, source.ReasoningEffort);

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

    private static string MakeLocalModelId(string providerId, string modelId)
        => modelId.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase)
            ? modelId
            : $"{providerId}/{modelId}";

    private static string ToApiFormatName(LlmApiFormat format)
        => format switch
        {
            LlmApiFormat.OpenAiChatCompletions => "openai-chat-completions",
            LlmApiFormat.OpenAiResponses => "openai-responses",
            LlmApiFormat.AnthropicMessages => "anthropic-messages",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static LlmApiFormat ParseApiFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("openai-chat-completions", StringComparison.OrdinalIgnoreCase))
        {
            return LlmApiFormat.OpenAiChatCompletions;
        }
        if (value.Equals("openai-responses", StringComparison.OrdinalIgnoreCase))
        {
            return LlmApiFormat.OpenAiResponses;
        }
        if (value.Equals("anthropic-messages", StringComparison.OrdinalIgnoreCase))
        {
            return LlmApiFormat.AnthropicMessages;
        }
        throw new NotSupportedException($"不支持的 API 格式: {value}");
    }

    private static string? NormalizeReasoningEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString().TrimEnd('/')
            : throw new ArgumentException("API 地址必须是 http 或 https URL。");
    }

    private static string RequireId(string value, string parameterName)
    {
        var result = RequireText(value, parameterName);
        if (result.Length > 200 || result.Any(char.IsControl))
        {
            throw new ArgumentException("标识符格式无效。", parameterName);
        }
        return result;
    }

    private static string RequireText(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("不能为空。", parameterName)
            : value.Trim();

    private static string Fingerprint(string secret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        var tail = secret.Length <= 4 ? secret : secret[^4..];
        return $"…{tail} ({hash[..8]})";
    }

    private sealed class ProviderRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public LlmApiFormat ApiFormat { get; set; }
        public bool Enabled { get; set; } = true;
        public string? CatalogProviderId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class ModelRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string RemoteModelId { get; set; } = string.Empty;
        public int ContextLength { get; set; }
        public int MaxOutputTokens { get; set; }
        public LlmModelCapabilities Capabilities { get; set; }
        /// <summary>深度思考档位（low/medium/high），空表示不开启；agent 生成时透传 LlmOptions.ReasoningEffort</summary>
        public string? ReasoningEffort { get; set; }
        public bool Enabled { get; set; } = true;
        public string? CatalogProviderId { get; set; }
        public string? CatalogModelId { get; set; }
        public DateTimeOffset? CatalogUpdatedAtUtc { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class KeyRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ProtectedSecret { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public int Priority { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class MetaRecord
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed record CatalogImportRequest(string ProviderId, string ModelId, string? BaseUrl, string? ApiFormat, string? ApiKey, bool? Enabled);
    private sealed record SaveProviderRequest(string Name, string BaseUrl, string? ApiFormat, bool Enabled);
    private sealed record SaveModelRequest(string ProviderId, string Name, string RemoteModelId, int ContextLength, int MaxOutputTokens, LlmModelCapabilities Capabilities, bool Enabled, string? ReasoningEffort = null);
    private sealed record SaveKeyRequest(string ProviderId, string? Name, string Secret, int Priority, bool Enabled);

    private sealed record ConfigSnapshot(string? DefaultModelId, IReadOnlyList<ProviderDto> Providers);
    private sealed record ProviderDto(string Id, string Name, string BaseUrl, string ApiFormat, bool Enabled, string? CatalogProviderId, IReadOnlyList<ModelDto> Models, IReadOnlyList<KeyDto> Keys);
    private sealed record ModelDto(string Id, string ProviderId, string Name, string RemoteModelId, int ContextLength, int MaxOutputTokens, string Capabilities, bool Enabled, DateTimeOffset? CatalogUpdatedAtUtc, string? ReasoningEffort);
    private sealed record KeyDto(string Id, string Name, string Fingerprint, int Priority, bool Enabled, DateTimeOffset UpdatedAtUtc);
    private sealed record CatalogStatusDto(string Source, DateTimeOffset? UpdatedAtUtc, string? RefreshError);
    private sealed record CatalogProviderDto(string Id, string Name, string? SuggestedBaseUrl, int ModelCount);
    private sealed record CatalogModelDto(string ProviderId, string ProviderName, string ModelId, string Name, string? SuggestedBaseUrl, int ContextLength, int MaxOutputTokens, string Capabilities, bool ToolCall, bool Reasoning);
}
