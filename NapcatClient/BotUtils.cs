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
    public static string Serialize<T>(T obj)
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
    public static void ParseDynamicJsonValue(IEnumerable<Message> messages)
    {
        foreach (var item in messages)
        {
            item.ParseJsonDynamic();
        }
    }
    /// <summary>
    /// 拼接连续的text消息
    /// </summary>
    /// <param name="raw"></param>
    /// <returns></returns>
    internal static List<Message> ConcatAdjacencyText(List<Message> raw)
    {
        List<Message> result = [];
        StringBuilder sb = new();
        foreach( var i in raw)
        {
            if(i.MessageType == "text")
            {
                sb.Append(i.Data["text"]);
            }
            else
            {
                sb.Append(Message.Text(sb.ToString()));
                sb.Clear();
                result.Add(i);
            }
        }
        var tail=sb.ToString();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            result.Add(Message.Text(tail));
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