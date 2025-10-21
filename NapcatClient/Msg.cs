global using Detail = System.Collections.Generic.Dictionary<string, dynamic>;
global using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;
using CommonLib;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;
namespace NapcatClient;




/// <summary>
///  view https://napneko.github.io/onebot/sement for details
/// </summary>
public class Message
{
    [JsonPropertyName("type")]
    public string MessageType {set; get; }
    [JsonPropertyName("data")]
    public Detail Data {set; get; } = new();
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
        string v;
        switch (MessageType)
        {
            case Str.Text:
                v=Data["text"];
                break;
            case Str.At:
                v = Data["qq"];
                break;
            case Str.Image:
                v = Data["file"];
                break;
            default:
                v = Data.GetString();
                break;
        }
        return $"{MessageType}:{v}";
    }
    /// <summary>
    /// 将JsonElement解析为MessageChain
    /// </summary>
    /// <param name="messages"></param>
    /// <returns></returns>
    internal static List<Message> ParseMessageChain(JsonElement messages)
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
    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
    internal void ParseJsonDynamic()
    {
        foreach (var j in Data)
        {
            Data[j.Key] = JsonUtils.GetActualValue(j.Value);
        }
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
    public static Message Image(string base64, string? summary=null)
    {
        Message message = new Message("image");
        message.Data["file"] = $"base64://{base64}";
        if (summary != null)
        {
            message.Data["summary"]= summary;
        }
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
    public override bool Equals(object? obj)
    {
        var other= obj as Message;
        if (other == null) return false;
        if (other.MessageType == MessageType)
        {
            var keys=Data.Keys;
            var otherKey=other.Data.Keys;
            if (keys.Count != otherKey.Count) return false;
            foreach (var key in keys)
            {
                if (!otherKey.Contains(key)) return false;
                if (!Data[key].Equals(other.Data[key])) return false;
            }
            return true;
        }
        else
        {
            return false;
        }
    }
}

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
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

public class ResponseRootObject
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("retcode")]
    public int Retcode { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonPropertyName("wording")]
    public string Wording { get; set; }

    [JsonPropertyName("echo")]
    public string Echo { get; set; }
}

public class GroupMemberListData
{
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; }

    [JsonPropertyName("card")]
    public string Card { get; set; }

    [JsonPropertyName("sex")]
    public string Sex { get; set; }

    [JsonPropertyName("age")]
    public int Age { get; set; }

    [JsonPropertyName("area")]
    public string Area { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; }

    [JsonPropertyName("qq_level")]
    public int QqLevel { get; set; }

    [JsonPropertyName("join_time")]
    public long JoinTime { get; set; }

    [JsonPropertyName("last_sent_time")]
    public long LastSentTime { get; set; }

    [JsonPropertyName("title_expire_time")]
    public long TitleExpireTime { get; set; }

    [JsonPropertyName("unfriendly")]
    public bool Unfriendly { get; set; }

    [JsonPropertyName("card_changeable")]
    public bool CardChangeable { get; set; }

    [JsonPropertyName("is_robot")]
    public bool IsRobot { get; set; }

    [JsonPropertyName("shut_up_timestamp")]
    public long ShutUpTimestamp { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }
}


public class GroupMemberInfo
{
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; }

    [JsonPropertyName("card")]
    public string Card { get; set; }

    [JsonPropertyName("sex")]
    public string Sex { get; set; }

    [JsonPropertyName("age")]
    public int Age { get; set; }

    [JsonPropertyName("area")]
    public string Area { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; }

    [JsonPropertyName("qq_level")]
    public int QqLevel { get; set; }

    [JsonPropertyName("join_time")]
    public long JoinTime { get; set; }

    [JsonPropertyName("last_sent_time")]
    public long LastSentTime { get; set; }

    [JsonPropertyName("title_expire_time")]
    public long TitleExpireTime { get; set; }

    [JsonPropertyName("unfriendly")]
    public bool Unfriendly { get; set; }

    [JsonPropertyName("card_changeable")]
    public bool CardChangeable { get; set; }

    [JsonPropertyName("is_robot")]
    public bool IsRobot { get; set; }

    [JsonPropertyName("shut_up_timestamp")]
    public long ShutUpTimestamp { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }
}

public class GroupMessage
{
    [JsonPropertyName("self_id")]
    public long SelfId { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("message_seq")]
    public long MessageSeq { get; set; }

    [JsonPropertyName("real_id")]
    public long RealId { get; set; }

    [JsonPropertyName("real_seq")]
    public string RealSeq { get; set; }

    [JsonPropertyName("message_type")]
    public string MessageType { get; set; }

    [JsonPropertyName("sender")]
    public Sender SenderInfo { get; set; }

    [JsonPropertyName("raw_message")]
    public string RawMessage { get; set; }

    [JsonPropertyName("font")]
    public int Font { get; set; }

    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    [JsonPropertyName("message")]
    public List<Message> Message { get; set; }

    [JsonPropertyName("message_format")]
    public string MessageFormat { get; set; }

    [JsonPropertyName("post_type")]
    public string PostType { get; set; }

    [JsonPropertyName("message_sent_type")]
    public string MessageSentType { get; set; }

    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("group_name")]
    public string GroupName { get; set; }
}

public class ForwardMessage
{
    [JsonPropertyName("messages")]
    public List<GroupMessage> Messages { get; set; } = new();
}

#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。