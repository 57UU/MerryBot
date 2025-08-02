using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace NapcatClient;

public static class BotUtils
{
    static JsonSerializerOptions options;
    static BotUtils()
    {
        options = new JsonSerializerOptions()
        {
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
    }
    public static string Serilize<T>(T obj)
    {
        return JsonSerializer.Serialize<T>(obj, options);
    }
    public static long GetSelfId(ReceivedGroupMessage data)
    {
        return data.self_id;
    }
    public static string MessageChainToString(MessageChain chain)
    {
        var sb = new StringBuilder();
        foreach(var i in chain)
        {
            sb.Append(i.ToString());
            sb.Append(";");
        }
        return sb.ToString();
    }
}

public static class Extensions
{
    public static string GetString<K, V>(this IDictionary<K, V> dict)
    {
        var items = dict.Select(kvp => kvp.ToString());
        return string.Join(",", items);
    }
}