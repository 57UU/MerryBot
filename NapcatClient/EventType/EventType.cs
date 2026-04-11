using System.Text.Json.Serialization;

namespace NapcatClient.EventType;

#pragma warning disable CS8618

/// <summary>
/// 事件类型基类
/// </summary>
public class TypedEvent
{
    /// <summary>
    /// 事件发生时间戳
    /// </summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }

    /// <summary>
    /// 事件类型
    /// </summary>
    [JsonPropertyName("post_type")]
    public string PostType { get; set; }

    /// <summary>
    /// 收到事件的机器人 QQ 号
    /// </summary>
    [JsonPropertyName("self_id")]
    public long SelfId { get; set; }
}

/// <summary>
/// 通知事件基类
/// </summary>
public class NoticeEvent : TypedEvent
{
    /// <summary>
    /// 通知类型
    /// </summary>
    [JsonPropertyName("notice_type")]
    public string NoticeType { get; set; }
}

/// <summary>
/// 群文件上传事件
/// </summary>
public class GroupUploadEvent : NoticeEvent
{
    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 上传者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 文件信息
    /// </summary>
    [JsonPropertyName("file")]
    public FileInfo File { get; set; }

    /// <summary>
    /// 文件信息类
    /// </summary>
    public class FileInfo
    {
        /// <summary>
        /// 文件 ID
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// 文件名
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// 文件大小
        /// </summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>
        /// 文件 busid
        /// </summary>
        [JsonPropertyName("busid")]
        public long BusId { get; set; }
    }
}

/// <summary>
/// 群管理员变动事件
/// </summary>
public class GroupAdminEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，set 或 unset
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 操作者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
}

/// <summary>
/// 群成员减少事件
/// </summary>
public class GroupDecreaseEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，leave、kick 或 kick_me
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 操作者 QQ 号（如果是被踢，否则为 0）
    /// </summary>
    [JsonPropertyName("operator_id")]
    public long OperatorId { get; set; }

    /// <summary>
    /// 被操作 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
}

/// <summary>
/// 群成员增加事件
/// </summary>
public class GroupIncreaseEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，approve 或 invite
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 操作者 QQ 号
    /// </summary>
    [JsonPropertyName("operator_id")]
    public long OperatorId { get; set; }

    /// <summary>
    /// 被操作 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
}

/// <summary>
/// 群禁言事件
/// </summary>
public class GroupBanEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，ban 或 lift_ban
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 操作者 QQ 号
    /// </summary>
    [JsonPropertyName("operator_id")]
    public long OperatorId { get; set; }

    /// <summary>
    /// 被禁言 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 禁言时长（秒）
    /// </summary>
    [JsonPropertyName("duration")]
    public long Duration { get; set; }
}

/// <summary>
/// 新添加好友事件
/// </summary>
public class FriendAddEvent : NoticeEvent
{
    /// <summary>
    /// 新添加好友 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
}

/// <summary>
/// 群消息撤回事件
/// </summary>
public class GroupRecallEvent : NoticeEvent
{
    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 消息发送者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 操作者 QQ 号
    /// </summary>
    [JsonPropertyName("operator_id")]
    public long OperatorId { get; set; }

    /// <summary>
    /// 被撤回消息 ID
    /// </summary>
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }
}

/// <summary>
/// 好友消息撤回事件
/// </summary>
public class FriendRecallEvent : NoticeEvent
{
    /// <summary>
    /// 消息发送者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 被撤回消息 ID
    /// </summary>
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }
}

/// <summary>
/// 戳一戳事件
/// </summary>
public class PokeEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，poke
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号（如果是群聊）
    /// </summary>
    [JsonPropertyName("group_id")]
    public long? GroupId { get; set; }

    /// <summary>
    /// 戳人者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 被戳者 QQ 号
    /// </summary>
    [JsonPropertyName("target_id")]
    public long TargetId { get; set; }
}

/// <summary>
/// 运气王事件
/// </summary>
public class LuckyKingEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，lucky_king
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 红包发送者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 运气王 QQ 号
    /// </summary>
    [JsonPropertyName("target_id")]
    public long TargetId { get; set; }
}

/// <summary>
/// 荣誉变更事件
/// </summary>
public class HonorEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，honor
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 荣誉类型
    /// </summary>
    [JsonPropertyName("honor_type")]
    public string HonorType { get; set; }

    /// <summary>
    /// 获得荣誉者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }
}

/// <summary>
/// 群表情回应事件
/// </summary>
public class GroupMsgEmojiLikeEvent : NoticeEvent
{
    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 回应者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 被回应消息 ID
    /// </summary>
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    /// <summary>
    /// 表情 ID
    /// </summary>
    [JsonPropertyName("likes")]
    public string Likes { get; set; }

    /// <summary>
    /// 回应数量
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// 群精华事件
/// </summary>
public class EssenceEvent : NoticeEvent
{
    /// <summary>
    /// 子类型，add 或 delete
    /// </summary>
    [JsonPropertyName("sub_type")]
    public string SubType { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 消息 ID
    /// </summary>
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    /// <summary>
    /// 消息发送者 QQ 号
    /// </summary>
    [JsonPropertyName("sender_id")]
    public long SenderId { get; set; }

    /// <summary>
    /// 操作者 QQ 号
    /// </summary>
    [JsonPropertyName("operator_id")]
    public long OperatorId { get; set; }
}

/// <summary>
/// 群名片变更事件
/// </summary>
public class GroupCardEvent : NoticeEvent
{
    /// <summary>
    /// 群号
    /// </summary>
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 变更者 QQ 号
    /// </summary>
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 新群名片
    /// </summary>
    [JsonPropertyName("card_new")]
    public string CardNew { get; set; }

    /// <summary>
    /// 旧群名片
    /// </summary>
    [JsonPropertyName("card_old")]
    public string CardOld { get; set; }
}

#pragma warning restore CS8618