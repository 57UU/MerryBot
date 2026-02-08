using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace DataService;

public record GroupMessage(
    long GroupId,
    long SenderId,
    string SenderNickname,
    string SenderGroupNickname,
    string SenderGroupRole,
    [BsonId] long MessageId,
    List<NapcatClient.MessageType.TypedMessage> Messages,
    DateTime Time,
    bool IsDeleted = false
    )
{
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
            napcatGroupMessage.GroupId,
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
public record ImageEntry(
        [BsonId] long Id,
        string OriginalUrl,
        string Hash,
        byte[] Data
    );

public record FileEntry(
        [BsonId] long Id,
        string OriginalUrl,
        string Hash,
        byte[] Data
    );

public record GroupEvent(
    long GroupId,
    string EventType,
    string SubType,
    long UserId,
    long OperatorId,
    long? MessageId,
    long? Duration,
    string? Extra,
    DateTime Time,
    [BsonId] ObjectId Id = default
    );
