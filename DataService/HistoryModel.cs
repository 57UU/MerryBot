using LiteDB;
using NapcatClient;

namespace DataService;

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。

public class GroupMessage
{
    [BsonId]
    public ObjectId Id { get; set; }
    /// <summary>群号与消息 ID 组成的稳定业务键，用于幂等写入。</summary>
    public string MessageKey { get; set; } = string.Empty;
    public long MessageId { get; set; }
    public long GroupId { get; set; }
    public long SenderId { get; set; }
    public string SenderNickname { get; set; }
    public string SenderGroupNickname { get; set; }
    public string SenderGroupRole { get; set; }
    public List<NapcatClient.MessageType.TypedMessage> Messages { get; set; }
    public DateTime Time { get; set; }
    public bool IsDeleted { get; set; }

    public GroupMessage()
    {
        SenderNickname = string.Empty;
        SenderGroupNickname = string.Empty;
        SenderGroupRole = string.Empty;
        Messages = new List<NapcatClient.MessageType.TypedMessage>();
        IsDeleted = false;
    }

    public GroupMessage(
        long groupId,
        long senderId,
        string senderNickname,
        string senderGroupNickname,
        string senderGroupRole,
        long messageId,
        List<NapcatClient.MessageType.TypedMessage> messages,
        DateTime time,
        bool isDeleted = false)
    {
        GroupId = groupId;
        SenderId = senderId;
        SenderNickname = senderNickname;
        SenderGroupNickname = senderGroupNickname;
        SenderGroupRole = senderGroupRole;
        MessageId = messageId;
        Messages = messages;
        Time = time;
        IsDeleted = isDeleted;
        MessageKey = CreateMessageKey(groupId, messageId);
    }

    public static string CreateMessageKey(long groupId, long messageId) => $"g:{groupId}:m:{messageId}";

    public static GroupMessage FromReceivedGroupMessage(ReceivedGroupMessage receivedGroupMessage)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(receivedGroupMessage.time).UtcDateTime;
        return new GroupMessage(
            receivedGroupMessage.GroupId,
            receivedGroupMessage.sender.user_id,
            receivedGroupMessage.sender.nickname,
            receivedGroupMessage.sender.card,
            receivedGroupMessage.sender.role,
            receivedGroupMessage.message_id,
            receivedGroupMessage.message,
            time
        );
    }

    public static GroupMessage FromNapcatGroupMessage(NapcatClient.GroupMessage napcatGroupMessage)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(napcatGroupMessage.Time).UtcDateTime;
        return new GroupMessage(
            -1,//indicate forward message, it not belong to any group
            napcatGroupMessage.UserId,
            napcatGroupMessage.SenderInfo.nickname,
            napcatGroupMessage.SenderInfo.card,
            napcatGroupMessage.SenderInfo.role,
            napcatGroupMessage.MessageId,
            napcatGroupMessage.Message,
            time
        );
    }
}

public class ImageEntry
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; }
    public string Hash { get; set; }
    /// <summary>
    /// 图片文字描述，由 vision 模型生成后写入。null 表示尚未解析或解析失败。
    /// </summary>
    public string? Description { get; set; }

    public ImageEntry()
    {
        OriginalUrl = string.Empty;
        Hash = string.Empty;
    }

    public ImageEntry(long id, string originalUrl, string hash)
    {
        Id = id;
        OriginalUrl = originalUrl;
        Hash = hash;
    }
}

public class FileEntry
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; }
    public string Hash { get; set; }

    public FileEntry()
    {
        OriginalUrl = string.Empty;
        Hash = string.Empty;
    }

    public FileEntry(long id, string originalUrl, string hash)
    {
        Id = id;
        OriginalUrl = originalUrl;
        Hash = hash;
    }
}

public class GroupEvent
{

    public ObjectId Id { get; set; }
    public long GroupId { get; set; }
    public string EventType { get; set; }
    public string SubType { get; set; }
    public long UserId { get; set; }
    public long OperatorId { get; set; }
    public long? MessageId { get; set; }
    public long? Duration { get; set; }
    public string? Extra { get; set; }
    public DateTime Time { get; set; }

    public GroupEvent()
    {
        Id = ObjectId.NewObjectId();
        EventType = string.Empty;
        SubType = string.Empty;
    }

    public GroupEvent(
        long groupId,
        string eventType,
        string subType,
        long userId,
        long operatorId,
        long? messageId,
        long? duration,
        string? extra,
        DateTime time,
        ObjectId? id = default)
    {
        GroupId = groupId;
        EventType = eventType;
        SubType = subType;
        UserId = userId;
        OperatorId = operatorId;
        MessageId = messageId;
        Duration = duration;
        Extra = extra;
        Time = time;
        Id = id == default ? ObjectId.NewObjectId() : id;
    }
}

public class ForwardMessageEntry
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string ForwardId { get; set; }
    public long SourceGroupId { get; set; }
    public List<GroupMessage> Messages { get; set; }
    public DateTime Time { get; set; }

    public ForwardMessageEntry()
    {
        Id = ObjectId.NewObjectId();
        ForwardId = string.Empty;
        Messages = new List<GroupMessage>();
    }

    public ForwardMessageEntry(string forwardId, long sourceGroupId, List<GroupMessage> messages, DateTime time)
    {
        Id = ObjectId.NewObjectId();
        ForwardId = forwardId;
        SourceGroupId = sourceGroupId;
        Messages = messages;
        Time = time;
    }
}

/// <summary>
/// 本地资源 URI 与远端资源、对象存储记录之间的映射。
/// 下载状态由 Core 内存维护；数据库只记录可恢复的描述与已落地对象。
/// </summary>
public class ResourceReference
{
    [BsonId]
    public string LocalUri { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? OriginalName { get; set; }
    public long? StoredObjectId { get; set; }
    public bool IsImage { get; set; }
    public DateTime UpdatedTime { get; set; }
}


public class GroupNameEntry
{
    [BsonId]
    public long GroupId { get; set; }
    public string Name { get; set; }
    public int MemberCount { get; set; }
    public int MaxMemberCount { get; set; }
    public DateTime UpdatedTime { get; set; }
}

public class AiMessageEntry
{
    [BsonId]
    public long Id { get; set; }
    public string SessionKey { get; set; }
    public string MessageType { get; set; }
    public string Content { get; set; }
    public long Time { get; set; }
    /// <summary>输入 token 数（归一化约定：含缓存命中的完整输入，cached ⊆ input）。</summary>
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    /// <summary>缓存命中的输入 token 数。LiteDB 无 schema，旧文档读出为 0。</summary>
    public int CachedTokens { get; set; }

    public AiMessageEntry()
    {
        SessionKey = string.Empty;
        MessageType = string.Empty;
        Content = string.Empty;
    }

    public AiMessageEntry(long id, string sessionKey, string messageType, string content, long time,
        int inputTokens = 0, int outputTokens = 0, int cachedTokens = 0)
    {
        Id = id;
        SessionKey = sessionKey;
        MessageType = messageType;
        Content = content;
        Time = time;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
    }
}

/// <summary>单个时间桶的 token 用量；uncached 已在聚合层算好（max(0, input - cached)）。</summary>
public sealed record TokenUsageBucket(long BucketStart, long CachedTokens, long UncachedInputTokens, long OutputTokens)
{
    public long TotalTokens => CachedTokens + UncachedInputTokens + OutputTokens;
}

/// <summary>时间范围内单会话的 token 用量汇总。</summary>
public sealed record TokenSessionSummary(string SessionKey, long CachedTokens, long UncachedInputTokens, long OutputTokens, int MessageCount, long LastTime)
{
    public long TotalTokens => CachedTokens + UncachedInputTokens + OutputTokens;
}

#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
