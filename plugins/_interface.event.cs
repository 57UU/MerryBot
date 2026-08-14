using NapcatClient;
using NapcatClient.EventType;

namespace BotPlugin;

/// <summary>
/// 插件事件订阅器：宿主（Logic）持有单个共享实例，收到机器人事件后调用 RaiseXxx 触发；
/// 插件通过 interop.EventRegister.OnXxxReceived += handler 订阅、-= 退订。
/// 事件回调为同步执行，异常由订阅方自行处理。
/// </summary>
public class EventRegister
{
    /// <summary>通知类事件基类，收到任意通知事件都会触发</summary>
    public event Action<NoticeEvent>? OnNoticeEventReceived;
    public event Action<GroupUploadEvent>? OnGroupUploadEventReceived;
    public event Action<GroupAdminEvent>? OnGroupAdminEventReceived;
    public event Action<GroupDecreaseEvent>? OnGroupDecreaseEventReceived;
    public event Action<GroupIncreaseEvent>? OnGroupIncreaseEventReceived;
    public event Action<GroupBanEvent>? OnGroupBanEventReceived;
    public event Action<FriendAddEvent>? OnFriendAddEventReceived;
    public event Action<GroupRecallEvent>? OnGroupRecallEventReceived;
    public event Action<FriendRecallEvent>? OnFriendRecallEventReceived;
    public event Action<PokeEvent>? OnPokeEventReceived;
    public event Action<LuckyKingEvent>? OnLuckyKingEventReceived;
    public event Action<HonorEvent>? OnHonorEventReceived;
    public event Action<GroupMsgEmojiLikeEvent>? OnGroupMsgEmojiLikeEventReceived;
    public event Action<EssenceEvent>? OnEssenceEventReceived;
    public event Action<GroupCardEvent>? OnGroupCardEventReceived;

    public void RaiseNoticeEventReceived(NoticeEvent eventData) => OnNoticeEventReceived?.Invoke(eventData);
    public void RaiseGroupUploadEventReceived(GroupUploadEvent eventData) => OnGroupUploadEventReceived?.Invoke(eventData);
    public void RaiseGroupAdminEventReceived(GroupAdminEvent eventData) => OnGroupAdminEventReceived?.Invoke(eventData);
    public void RaiseGroupDecreaseEventReceived(GroupDecreaseEvent eventData) => OnGroupDecreaseEventReceived?.Invoke(eventData);
    public void RaiseGroupIncreaseEventReceived(GroupIncreaseEvent eventData) => OnGroupIncreaseEventReceived?.Invoke(eventData);
    public void RaiseGroupBanEventReceived(GroupBanEvent eventData) => OnGroupBanEventReceived?.Invoke(eventData);
    public void RaiseFriendAddEventReceived(FriendAddEvent eventData) => OnFriendAddEventReceived?.Invoke(eventData);
    public void RaiseGroupRecallEventReceived(GroupRecallEvent eventData) => OnGroupRecallEventReceived?.Invoke(eventData);
    public void RaiseFriendRecallEventReceived(FriendRecallEvent eventData) => OnFriendRecallEventReceived?.Invoke(eventData);
    public void RaisePokeEventReceived(PokeEvent eventData) => OnPokeEventReceived?.Invoke(eventData);
    public void RaiseLuckyKingEventReceived(LuckyKingEvent eventData) => OnLuckyKingEventReceived?.Invoke(eventData);
    public void RaiseHonorEventReceived(HonorEvent eventData) => OnHonorEventReceived?.Invoke(eventData);
    public void RaiseGroupMsgEmojiLikeEventReceived(GroupMsgEmojiLikeEvent eventData) => OnGroupMsgEmojiLikeEventReceived?.Invoke(eventData);
    public void RaiseEssenceEventReceived(EssenceEvent eventData) => OnEssenceEventReceived?.Invoke(eventData);
    public void RaiseGroupCardEventReceived(GroupCardEvent eventData) => OnGroupCardEventReceived?.Invoke(eventData);
}
