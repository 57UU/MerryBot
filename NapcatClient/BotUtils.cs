using Newtonsoft.Json;

namespace NapcatClient;

public static class BotUtils
{
    public static string serilize<T>(T obj)
    {
        return JsonConvert.SerializeObject(obj);
    }
    public static long GetSelfId(Dictionary<string, dynamic> data)
    {
        return data["self_id"];
    }

}
