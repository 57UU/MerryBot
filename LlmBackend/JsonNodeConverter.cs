using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmBackend;

/// <summary>
/// 把运行时构建的 Dictionary/List 请求体树转换为 <see cref="JsonNode"/> 树,
/// 供 NativeAOT 下以 ToJsonString() 序列化——不依赖反射式 JsonSerializer。
/// 覆盖 LlmBackend 请求构建实际使用的值类型(Dictionary、List、JsonElement、标量)。
/// </summary>
internal static class JsonNodeConverter
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText()),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create(m),
        float f => JsonValue.Create(f),
        Guid g => JsonValue.Create(g),
        DateTimeOffset dto => JsonValue.Create(dto),
        IDictionary<string, object> dict => ToObject(dict),
        IEnumerable list => ToArray(list),
        _ => throw new NotSupportedException($"JsonNodeConverter: 不支持的值类型 {value.GetType().FullName}"),
    };

    private static JsonObject ToObject(IDictionary<string, object> dict)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in dict)
        {
            obj[key] = ToNode(value);
        }
        return obj;
    }

    private static JsonArray ToArray(IEnumerable list)
    {
        var arr = new JsonArray();
        foreach (var item in list)
        {
            arr.Add(ToNode(item));
        }
        return arr;
    }
}
