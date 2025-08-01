using Newtonsoft.Json;
using System.Text;

namespace NapcatClient;

public static class BotUtils
{
    public static string serilize<T>(T obj)
    {
        return JsonConvert.SerializeObject(obj);
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