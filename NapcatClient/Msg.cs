global using MessageChain = System.ReadOnlySpan<NapcatClient.MessageType.TypedMessage>;
using NapcatClient.MessageType;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace NapcatClient;




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
    public List<TypedMessage> message { get; set; } = new();
    public string message_format { get; set; }
    public string post_type { get; set; }
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }
    public dynamic raw { get; set; }
}

public class GroupForwardChain
{
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
    public class Builder
    {
        public readonly string userId;
        public readonly string nickname;
        private readonly GroupForwardChain chain = new();
        public Builder(string selfId, string nickname, string groupId)
        {
            userId = selfId;
            this.nickname = nickname;
            chain.GroupId = groupId;
            chain.Prompt = "我喜欢你很久了，能不能做我男朋友";
            chain.Summary = "思考结果";
            chain.Source = "聊天记录";
        }
        public void AddText(string text)
        {
            MessageItem messageItem = new();
            chain.Messages.Add(messageItem);
            messageItem.Data.NickName = nickname;
            messageItem.Data.UserId = userId;
            messageItem.Data.Content = TextData.FromText(text);
            chain.News.Add(new Dictionary<string, object>() { { "text", $"{nickname}:{text}" } });
        }
        public GroupForwardChain Build()
        {
            return chain;
        }
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
    public TypedMessage Content { get; set; }
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
    public List<TypedMessage> Message { get; set; }

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

public class GroupInfo
{
    [JsonPropertyName("group_all_shut")]
    public int GroupAllShut { get; set; }

    [JsonPropertyName("group_remark")]
    public string GroupRemark { get; set; }

    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("group_name")]
    public string GroupName { get; set; }

    [JsonPropertyName("member_count")]
    public int MemberCount { get; set; }

    [JsonPropertyName("max_member_count")]
    public int MaxMemberCount { get; set; }
}

#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。