using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelsDev.Sdk.Models;

/// <summary>
/// Describes how interleaved content (e.g. reasoning traces) is embedded in responses.
/// </summary>
[JsonConverter(typeof(InterleavedConfigConverter))]
public sealed class InterleavedConfig
{
    /// <summary>Whether the provider supports interleaved content.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The JSON field name that contains interleaved content, when the catalog
    /// provides one. Some providers expose this capability as <c>true</c>
    /// without naming a field.
    /// </summary>
    public string? Field { get; init; }
}

/// <summary>
/// The models.dev catalog permits <c>interleaved</c> to be either a boolean or
/// an object containing a field name. This converter supports both wire forms.
/// </summary>
internal sealed class InterleavedConfigConverter : JsonConverter<InterleavedConfig>
{
    public override InterleavedConfig? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => new InterleavedConfig { Enabled = true },
            JsonTokenType.False => new InterleavedConfig { Enabled = false },
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException("interleaved 必须是布尔值或包含 field 的对象。"),
        };
    }

    public override void Write(Utf8JsonWriter writer, InterleavedConfig value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Field))
        {
            writer.WriteBooleanValue(value.Enabled);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("field", value.Field);
        writer.WriteEndObject();
    }

    private static InterleavedConfig ReadObject(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (!root.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("interleaved 对象必须包含字符串 field。 ");
        }
        return new InterleavedConfig
        {
            Enabled = true,
            Field = field.GetString(),
        };
    }
}
