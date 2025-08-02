global using Detail = System.Collections.Generic.Dictionary<string, dynamic>;
global using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;
using CommonLib;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace NapcatClient;




/// <summary>
///  view https://napneko.github.io/onebot/sement for details
/// </summary>
public class Message
{
    [JsonPropertyName("type")]
    public string MessageType {set; get; }
    [JsonPropertyName("data")]
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
    public static List<Message> ParseMessageChain(JsonElement messages)
    {
        var chain = new List<Message>();
        foreach (JsonElement i in messages.EnumerateArray())
        {
            string type = i.GetProperty("type")!.GetString()!;
            var msg = new Message(type);
            var data = i.GetProperty("data")!.Deserialize<Dictionary<string, dynamic>>()!;
            foreach(var j in data)
            {
                msg.Data[j.Key]=JsonUtils.GetActualValue(j.Value);
            }
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

public class GroupForwardChain
{
    [JsonIgnore]
    public string Nickname { get; set; }
    [JsonIgnore]
    public string UserId { get; set; }


    [JsonPropertyName("group_id")]
    public string GroupId { get; set; }

    [JsonPropertyName("messages")]
    public List<MessageItem> Messages { get; set; } = new();

    [JsonPropertyName("news")]
    public List<Dictionary<string, object>> News { get; set; } = new();

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; }
    public static GroupForwardChain BuildDefault(string selfId,string nickname,string groupId)
    {
        return new GroupForwardChain
        {
            UserId = selfId,
            Nickname = nickname,
            GroupId = groupId,
            Prompt = "我喜欢你很久了，能不能做我男朋友",
            Summary = "思考结果",
            Source = "聊天记录"
        };
    }
    public void AddText(string text)
    {
        MessageItem messageItem = new();
        Messages.Add(messageItem);
        messageItem.Data.NickName = Nickname;
        messageItem.Data.UserId = UserId;
        messageItem.Data.Content = Message.Text(text);
        News.Add(new Dictionary<string, object>() { {"text",$"{Nickname}:{text}" } });
    }
}

public class MessageItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "node";

    [JsonPropertyName("data")]
    public MessageDataItem Data { get; set; } = new();
}

public class MessageDataItem
{
    [JsonPropertyName("user_id")]
    public string UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string NickName { get; set; }

    [JsonPropertyName("content")]
    public Message Content { get; set; }
}