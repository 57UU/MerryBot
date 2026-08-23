using LiteDB;
using LlmBackend;

namespace BotPlugin;

public sealed partial class LlmProviderPlugin
{
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
        /// <summary>models.dev 的 reasoning_options 原样落库（toggle/effort+values），WebUI 可直接编辑</summary>
        public List<StoredReasoningOption>? ReasoningOptions { get; set; }
        /// <summary>anthropic 格式启用显式 prompt 缓存（cache_control 断点）；其他格式忽略</summary>
        public bool EnablePromptCache { get; set; }
        public bool Enabled { get; set; } = true;
        public string? CatalogProviderId { get; set; }
        public string? CatalogModelId { get; set; }
        public DateTimeOffset? CatalogUpdatedAtUtc { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class StoredReasoningOption
    {
        public string Type { get; set; } = string.Empty;
        public List<string>? Values { get; set; }
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
