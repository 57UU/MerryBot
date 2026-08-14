using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Represents a single AI model with its full metadata.
/// </summary>
public sealed class ModelInfo
{
    /// <summary>Unique model identifier (may include provider prefix, e.g. "google/gemini-2.5-flash").</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable model name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Short description of the model's purpose and strengths.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Model family name (e.g. "gpt", "claude", "gemini-pro").</summary>
    [JsonPropertyName("family")]
    public string? Family { get; init; }

    /// <summary>Whether the model supports file/attachment inputs.</summary>
    [JsonPropertyName("attachment")]
    public bool Attachment { get; init; }

    /// <summary>Whether the model supports chain-of-thought / reasoning mode.</summary>
    [JsonPropertyName("reasoning")]
    public bool Reasoning { get; init; }

    /// <summary>Configuration options for reasoning mode (toggle, effort levels, etc.).</summary>
    [JsonPropertyName("reasoning_options")]
    public IReadOnlyList<ReasoningOption> ReasoningOptions { get; init; } = [];

    /// <summary>Whether the model supports tool/function calling.</summary>
    [JsonPropertyName("tool_call")]
    public bool ToolCall { get; init; }

    /// <summary>Whether the model supports structured (JSON) output.</summary>
    [JsonPropertyName("structured_output")]
    public bool StructuredOutput { get; init; }

    /// <summary>Whether the model supports temperature parameter.</summary>
    [JsonPropertyName("temperature")]
    public bool Temperature { get; init; }

    /// <summary>Interleaved content configuration (e.g. reasoning content field name).</summary>
    [JsonPropertyName("interleaved")]
    public InterleavedConfig? Interleaved { get; init; }

    /// <summary>Knowledge cutoff date string (e.g. "2025-04").</summary>
    [JsonPropertyName("knowledge")]
    public string? Knowledge { get; init; }

    /// <summary>Model release date (e.g. "2025-07-28").</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    /// <summary>Date the model metadata was last updated.</summary>
    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; init; }

    /// <summary>Supported input and output modalities.</summary>
    [JsonPropertyName("modalities")]
    public Modalities? Modalities { get; init; }

    /// <summary>Whether the model weights are publicly available.</summary>
    [JsonPropertyName("open_weights")]
    public bool? OpenWeights { get; init; }

    /// <summary>Token limits for context window and output.</summary>
    [JsonPropertyName("limit")]
    public ModelLimits? Limit { get; init; }

    /// <summary>Pricing information per million tokens.</summary>
    [JsonPropertyName("cost")]
    public ModelCost? Cost { get; init; }

    /// <summary>Model status (e.g. "deprecated", "preview").</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
