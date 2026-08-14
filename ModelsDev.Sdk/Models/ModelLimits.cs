using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Token count limits for a model's context window and output.
/// </summary>
public sealed class ModelLimits
{
    /// <summary>Maximum context window size in tokens.</summary>
    [JsonPropertyName("context")]
    public int Context { get; init; }

    /// <summary>Maximum output token count.</summary>
    [JsonPropertyName("output")]
    public int Output { get; init; }
}
