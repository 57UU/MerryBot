using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Pricing information for a model. All costs are in USD per million tokens.
/// </summary>
public sealed class ModelCost
{
    /// <summary>Cost per million input tokens.</summary>
    [JsonPropertyName("input")]
    public decimal Input { get; init; }

    /// <summary>Cost per million output tokens.</summary>
    [JsonPropertyName("output")]
    public decimal Output { get; init; }

    /// <summary>Cost per million tokens for cache reads.</summary>
    [JsonPropertyName("cache_read")]
    public decimal? CacheRead { get; init; }

    /// <summary>Cost per million tokens for cache writes.</summary>
    [JsonPropertyName("cache_write")]
    public decimal? CacheWrite { get; init; }
}
