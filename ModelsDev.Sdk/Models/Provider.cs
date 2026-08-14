using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Represents a provider (e.g. OpenAI, Anthropic, Google) that hosts one or more AI models.
/// </summary>
public sealed class Provider
{
    /// <summary>Provider identifier key (e.g. "openai", "anthropic").</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable provider name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Environment variable names required for API authentication.</summary>
    [JsonPropertyName("env")]
    public IReadOnlyList<string> Env { get; init; } = [];

    /// <summary>NPM package name for the AI SDK integration.</summary>
    [JsonPropertyName("npm")]
    public string? Npm { get; init; }

    /// <summary>Base API endpoint URL.</summary>
    [JsonPropertyName("api")]
    public string? Api { get; init; }

    /// <summary>URL to the provider's official documentation.</summary>
    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    /// <summary>All models offered by this provider, keyed by model ID.</summary>
    [JsonPropertyName("models")]
    public IReadOnlyDictionary<string, ModelInfo> Models { get; init; } = new Dictionary<string, ModelInfo>();
}
