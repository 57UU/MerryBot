global using Detail = System.Collections.Generic.Dictionary<string, dynamic>;
global using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
namespace NapcatClient;




/// <summary>
///  view https://napneko.github.io/onebot/sement for details
/// </summary>
public class Message
{
    [JsonProperty(PropertyName = "type")]
    public string MessageType {set; get; }
    [JsonProperty(PropertyName = "data")]
    public Detail Data {internal set; get; } = new();
    public Message(string messageType)
    {
        this.MessageType = messageType;
    }
    public Message()
    {

    }
    public Message(string messageType, Dictionary<string, dynamic> data)
    {
        this.MessageType = messageType;
        this.Data = data;
    }
    public override string ToString()
    {
        return ToPreviewText();
    }
    public string ToPreviewText()
    {
        StringBuilder stringBuilder = new StringBuilder($"{MessageType}:");
        switch (MessageType)
        {
            case Str.Text:
                stringBuilder.Append(Data["text"]);
                break;
            case Str.At:
                stringBuilder.Append(Data["qq"]);
                break;
            case Str.Image:
                stringBuilder.Append(Data["file"]);
                break;
            default: 
                stringBuilder.Append(Data.GetString());
                break;
        }
        return stringBuilder.ToString();
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
    public static Message Image(string base64, string summary)
    {
        Message message = new Message("reply");
        message.Data["file"] = $"base64://{base64}";
        return message;
    }
    public static Message Reply(long id)
    {
        Message message = new Message("reply");
        message.Data["id"] = id;
        return message;
    }
    public static class Str
    {
        public const string Image = "image";
        public const string Text = "text";
        public const string At = "at";
    } 
}


public class Sender
{
    public long user_id { get; set; }
    public string nickname { get; set; }
    public string card { get; set; }
    public string role { get; set; }
}

public class ReceivedGroupMessage
{
    public long self_id { get; set; }
    public long user_id { get; set; }
    public long time { get; set; }
    public long message_id { get; set; }
    public long message_seq { get; set; }
    public long real_id { get; set; }
    public string real_seq { get; set; }
    public string message_type { get; set; }
    public Sender sender { get; set; } = new();
    public string raw_message { get; set; }
    public int font { get; set; }
    public string sub_type { get; set; }
    public List<Message> message { get; set; } = new();
    public string message_format { get; set; }
    public string post_type { get; set; }
    public long group_id { get; set; }
    public dynamic raw { get; set; }
}