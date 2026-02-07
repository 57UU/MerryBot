using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NapcatClient.MessageType;

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
            Converters = { TypedJsonConverter.Instance },
        };
    }
    public static string Serialize<T>(T obj)
    {
        return JsonSerializer.Serialize<T>(obj, options);
    }
    public static T Deserialize<T>(string text)
    {
        return JsonSerializer.Deserialize<T>(text, options)!;
    }
        public static T Deserialize<T>(JsonElement text)
    {
        return JsonSerializer.Deserialize<T>(text, options)!;
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
    /// <summary>
    /// 拼接连续的text消息
    /// </summary>
    /// <param name="raw"></param>
    /// <returns></returns>
    internal static List<TypedMessage> ConcatAdjacencyText(List<TypedMessage> raw)
    {
        List<TypedMessage> result = [];
        StringBuilder sb = new();
        foreach( var i in raw)
        {
            if(i is TextData textData)
            {
                sb.Append(textData.Text);
            }
            else
            {
                // 当遇到非text类型消息时，如果之前有积累的文本，将其添加到结果列表
                if (sb.Length > 0)
                {
                    result.Add(TextData.FromText(sb.ToString()));
                    sb.Clear();
                }
                result.Add(i);
            }
        }
        var tail=sb.ToString();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            result.Add(TextData.FromText(tail));
        }
        return result;
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