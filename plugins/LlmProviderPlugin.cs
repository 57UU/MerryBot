using LiteDB;
using LiteDB.Async;
using LlmBackend;
using LlmClient;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;

namespace BotPlugin;

/// <summary>
/// 管理可执行 LLM Provider、模型和 API Key。
/// 外部模型目录由 WebUI 查询；插件只保存已解析的本地配置。
/// </summary>
[PluginTag("llm-provider", "LLM Provider", "管理 LLM Provider、模型和 Key")]
public sealed class LlmProviderPlugin : Plugin, ILlmProviderRegistry, ILlmProviderManagementService
{
    private const string DefaultModelMetaId = "default-model";
    private const string SchemaVersionMetaId = "schema-version";
    private const string SchemaVersion = "1";
    private readonly ILiteCollectionAsync<ProviderRecord> providers;
    private readonly ILiteCollectionAsync<ModelRecord> models;
    private readonly ILiteCollectionAsync<KeyRecord> keys;
    private readonly ILiteCollectionAsync<MetaRecord> meta;
    private readonly IDataProtector keyProtector;

    public LlmProviderPlugin(PluginInterop interop) : base(interop)
    {
        providers = interop.PluginStorage.PluginDatabaseScope.GetCollection<ProviderRecord>("providers");
        models = interop.PluginStorage.PluginDatabaseScope.GetCollection<ModelRecord>("models");
        keys = interop.PluginStorage.PluginDatabaseScope.GetCollection<KeyRecord>("keys");
        meta = interop.PluginStorage.PluginDatabaseScope.GetCollection<MetaRecord>("meta");

        Directory.CreateDirectory(interop.PathPrefix);
        var keyRingPath = Path.Combine(interop.PathPrefix, "llm-provider-key-ring");
        Directory.CreateDirectory(keyRingPath);
        keyProtector = DataProtectionProvider
            .Create(new DirectoryInfo(keyRingPath), builder => builder.SetApplicationName("MerryBot.LlmProvider"))
            .CreateProtector("api-key.v1");

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
            LlmApiFormat.AnthropicMessages => new AnthropicBackend(provider.BaseUrl, apiKey, model.RemoteModelId, model.MaxOutputTokens, model.EnablePromptCache),
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

    public async Task<LlmProviderConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allProviders = await providers.FindAllAsync();
        var allModels = await models.FindAllAsync();
        var allKeys = await keys.FindAllAsync();
        var defaultModelId = await GetDefaultModelIdAsync(cancellationToken);
        var configuredProviders = allProviders
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new LlmProviderConfigurationProvider(
                provider.Id,
                provider.Name,
                provider.BaseUrl,
                provider.ApiFormat,
                provider.Enabled,
                provider.CatalogProviderId,
                allModels.Where(model => model.ProviderId == provider.Id)
                    .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(model => new LlmProviderConfigurationModel(
                        model.Id,
                        model.ProviderId,
                        model.Name,
                        model.RemoteModelId,
                        model.ContextLength,
                        model.MaxOutputTokens,
                        model.Capabilities,
                        model.Enabled,
                        model.CatalogUpdatedAtUtc,
                        model.ReasoningEffort,
                        model.EnablePromptCache))
                    .ToList(),
                allKeys.Where(key => key.ProviderId == provider.Id)
                    .OrderBy(key => key.Priority)
                    .Select(key => new LlmProviderConfigurationKey(key.Id, key.Name, key.Fingerprint, key.Priority, key.Enabled, key.UpdatedAtUtc))
                    .ToList()))
            .ToList();
        return new LlmProviderConfiguration(defaultModelId, configuredProviders);
    }

    public async Task<LlmProviderConfiguration> ImportCatalogModelAsync(
        LlmProviderCatalogImportCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalogProviderId = RequireId(command.ProviderId, nameof(command.ProviderId));
        var catalogModelId = RequireId(command.ModelId, nameof(command.ModelId));

        var now = DateTimeOffset.UtcNow;
        var provider = await providers.FindByIdAsync(catalogProviderId);
        if (provider == null)
        {
            provider = new ProviderRecord
            {
                Id = catalogProviderId,
                Name = RequireText(command.ProviderName, nameof(command.ProviderName)),
                BaseUrl = NormalizeUrl(command.BaseUrl) ?? NormalizeUrl(command.SuggestedBaseUrl) ?? string.Empty,
                ApiFormat = ParseApiFormat(command.ApiFormat),
                CatalogProviderId = catalogProviderId,
                Enabled = command.Enabled ?? true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
        else
        {
            // 目录导入只更新目录标识和显示名称，不覆盖用户手工填写的地址/格式/启用状态。
            provider.Name = RequireText(command.ProviderName, nameof(command.ProviderName));
            provider.CatalogProviderId = catalogProviderId;
            provider.UpdatedAtUtc = now;
        }
        await providers.UpsertAsync(provider);

        var localModelId = MakeLocalModelId(catalogProviderId, catalogModelId);
        var model = await models.FindByIdAsync(localModelId) ?? new ModelRecord
        {
            Id = localModelId,
            ProviderId = provider.Id,
            CreatedAtUtc = now,
        };
        model.Name = RequireText(command.ModelName, nameof(command.ModelName));
        model.RemoteModelId = catalogModelId;
        model.ContextLength = command.ContextLength > 0 ? command.ContextLength : 32_768;
        model.MaxOutputTokens = command.MaxOutputTokens > 0 ? command.MaxOutputTokens : 4_096;
        model.Capabilities = command.Capabilities;
        model.CatalogProviderId = catalogProviderId;
        model.CatalogModelId = catalogModelId;
        model.CatalogUpdatedAtUtc = command.CatalogUpdatedAtUtc;
        model.Enabled = command.Enabled ?? model.Enabled || model.CreatedAtUtc == now;
        model.UpdatedAtUtc = now;
        await models.UpsertAsync(model);

        if (!string.IsNullOrWhiteSpace(command.ApiKey))
        {
            await SaveKeyAsync(new LlmProviderKeySaveCommand(provider.Id, "默认 Key", command.ApiKey, 0, true), cancellationToken);
        }
        return await GetConfigurationAsync(cancellationToken);
    }

    public async Task SaveProviderAsync(string id, LlmProviderSaveCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = RequireId(id, nameof(id));
        var now = DateTimeOffset.UtcNow;
        var provider = await providers.FindByIdAsync(providerId) ?? new ProviderRecord
        {
            Id = providerId,
            CreatedAtUtc = now,
        };
        provider.Name = RequireText(command.Name, nameof(command.Name));
        provider.BaseUrl = NormalizeUrl(command.BaseUrl) ?? throw new ArgumentException("API 地址不能为空", nameof(command.BaseUrl));
        provider.ApiFormat = ParseApiFormat(command.ApiFormat);
        provider.Enabled = command.Enabled;
        provider.UpdatedAtUtc = now;
        await providers.UpsertAsync(provider);
    }

    public async Task SaveModelAsync(string id, LlmModelSaveCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelId = RequireId(id, nameof(id));
        var providerId = RequireId(command.ProviderId, nameof(command.ProviderId));
        if (await providers.FindByIdAsync(providerId) == null)
        {
            throw new KeyNotFoundException($"未找到 Provider: {providerId}");
        }
        if (command.ContextLength < 1 || command.MaxOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "上下文长度和最大输出必须为正数。");
        }
        var now = DateTimeOffset.UtcNow;
        var model = await models.FindByIdAsync(modelId) ?? new ModelRecord
        {
            Id = modelId,
            CreatedAtUtc = now,
        };
        model.ProviderId = providerId;
        model.Name = RequireText(command.Name, nameof(command.Name));
        model.RemoteModelId = RequireText(command.RemoteModelId, nameof(command.RemoteModelId));
        model.ContextLength = command.ContextLength;
        model.MaxOutputTokens = command.MaxOutputTokens;
        model.Capabilities = command.Capabilities;
        model.ReasoningEffort = NormalizeReasoningEffort(command.ReasoningEffort);
        model.EnablePromptCache = command.EnablePromptCache;
        model.Enabled = command.Enabled;
        model.UpdatedAtUtc = now;
        await models.UpsertAsync(model);
    }

    public async Task<LlmProviderConfigurationKey> SaveKeyAsync(
        LlmProviderKeySaveCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = RequireId(command.ProviderId, nameof(command.ProviderId));
        if (await providers.FindByIdAsync(providerId) == null)
        {
            throw new KeyNotFoundException($"未找到 Provider: {providerId}");
        }
        var secret = RequireText(command.Secret, nameof(command.Secret));
        var now = DateTimeOffset.UtcNow;
        var name = string.IsNullOrWhiteSpace(command.Name) ? "API Key" : command.Name.Trim();
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
        record.Priority = command.Priority;
        record.Enabled = command.Enabled;
        record.UpdatedAtUtc = now;
        await keys.UpsertAsync(record);
        return new LlmProviderConfigurationKey(record.Id, record.Name, record.Fingerprint, record.Priority, record.Enabled, record.UpdatedAtUtc);
    }

    public async Task DeleteProviderAsync(string id, CancellationToken cancellationToken = default)
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

    public async Task DeleteModelAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelId = RequireId(id, nameof(id));
        await models.DeleteAsync(modelId);
        await ClearDefaultModelIfAsync(modelId);
    }

    public async Task DeleteKeyAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await keys.DeleteAsync(RequireId(id, nameof(id)));
    }

    public async Task SetDefaultModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = await models.FindByIdAsync(RequireId(modelId, nameof(modelId)))
            ?? throw new KeyNotFoundException($"未找到模型: {modelId}");
        await meta.UpsertAsync(new MetaRecord { Id = DefaultModelMetaId, Value = model.Id });
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

    private static LlmModelDescriptor ToDescriptor(ModelRecord source)
        => new(source.Id, source.ProviderId, source.Name, source.RemoteModelId, source.ContextLength, source.MaxOutputTokens, source.Capabilities, source.Enabled, source.ReasoningEffort);

    private static string MakeLocalModelId(string providerId, string modelId)
        => modelId.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase)
            ? modelId
            : $"{providerId}/{modelId}";

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
        /// <summary>anthropic 格式启用显式 prompt 缓存（cache_control 断点）；其他格式忽略</summary>
        public bool EnablePromptCache { get; set; }
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

}
