using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Describes how interleaved content (e.g. reasoning traces) is embedded in responses.
/// </summary>
public sealed class InterleavedConfig
{
    /// <summary>The JSON field name that contains interleaved content.</summary>
    [JsonPropertyName("field")]
    public required string Field { get; init; }
}
