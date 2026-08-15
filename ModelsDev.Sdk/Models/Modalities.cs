using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Supported input and output modalities for a model.
/// </summary>
public sealed class Modalities
{
    /// <summary>Input modality types (e.g. "text", "image", "audio", "video", "pdf").</summary>
    [JsonPropertyName("input")]
    public IReadOnlyList<string> Input { get; init; } = [];

    /// <summary>Output modality types (typically just "text").</summary>
    [JsonPropertyName("output")]
    public IReadOnlyList<string> Output { get; init; } = [];
}
