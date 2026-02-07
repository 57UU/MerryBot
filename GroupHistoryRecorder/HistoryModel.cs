using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace GroupHistoryRecorder;

public record GroupMessage(
    long GroupId,
    long SenderId,
    string SenderNickname,
    string SenderGroupNickname,
    string SenderGroupRole,
    [BsonId] long MessageId,
    List<NapcatClient.MessageType.TypedMessage> Messages,
    DateTime Time
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
}
public record ImageEntry(
        [BsonId]string Url,
        byte[] Data
    );

public record FileEntry(
        [BsonId] string Url,
        byte[] Data
    );