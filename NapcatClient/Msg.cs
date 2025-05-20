using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NapcatClient;

/// <summary>
///  view https://napneko.github.io/onebot/sement for details
/// </summary>
public class Message
{
    [JsonProperty(PropertyName = "type")]
    public string MessageType { set; get; }
    [JsonProperty(PropertyName = "data")]
    public Dictionary<string, dynamic> Data { set; get; } = new();
    public Message(string messageType)
    {
        this.MessageType = messageType;
    }
    public static List<Message> ParseMessageChain(dynamic messages)
    {
        var chain = new List<Message>();
        foreach (JObject i in messages)
        {
            string type = i["type"]!.Value<string>()!;
            var msg = new Message(type);
            var data = i["data"]!.ToObject<Dictionary<string, dynamic>>()!;
            msg.Data = data;
            chain.Add(msg);
        }
        return chain;
    }
    public static Message Text(string text)
    {
        Message message = new Message("text");
        message.Data["text"] = text;
        return message;
    }
    public static Message At(string target)
    {
        Message message = new Message("at");
        message.Data["qq"] = target;
        return message;
    }
    public static Message Reply(string base64, string summary)
    {
        Message message = new Message("image");
        message.Data["file"] = $"base64://{base64}";
        message.Data["summary"] = summary;
        return message;
    }
}
