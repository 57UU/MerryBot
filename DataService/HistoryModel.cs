using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace DataService;

public class GroupMessage
{
    [BsonId]
    public ObjectId Id { get; set; }
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
    }

    public static GroupMessage FromReceivedGroupMessage(ReceivedGroupMessage receivedGroupMessage)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(receivedGroupMessage.time).UtcDateTime;
        return new GroupMessage(
            receivedGroupMessage.group_id,
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
    public byte[] Data { get; set; }

    public ImageEntry()
    {
        OriginalUrl = string.Empty;
        Hash = string.Empty;
        Data = Array.Empty<byte>();
    }

    public ImageEntry(long id, string originalUrl, string hash, byte[] data)
    {
        Id = id;
        OriginalUrl = originalUrl;
        Hash = hash;
        Data = data;
    }
}

public class FileEntry
{
    
    public long Id { get; set; }
    public string OriginalUrl { get; set; }
    public string Hash { get; set; }
    public byte[] Data { get; set; }

    public FileEntry()
    {
        OriginalUrl = string.Empty;
        Hash = string.Empty;
        Data = Array.Empty<byte>();
    }

    public FileEntry(long id, string originalUrl, string hash, byte[] data)
    {
        Id = id;
        OriginalUrl = originalUrl;
        Hash = hash;
        Data = data;
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
        ObjectId id = default)
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
