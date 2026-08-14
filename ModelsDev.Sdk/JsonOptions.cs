using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelsDev.Sdk;

/// <summary>
/// JSON serialization options configured for the models.dev API format.
/// </summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
