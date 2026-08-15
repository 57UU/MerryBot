using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Describes a reasoning mode option.
/// </summary>
public sealed class ReasoningOption
{
    /// <summary>Option type: "toggle" for on/off, "effort" for multi-level effort.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Allowed effort values when type is "effort" (e.g. ["high", "max"]).</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<string>? Values { get; init; }
}
