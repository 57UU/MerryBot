

namespace MerryBot;

internal partial class Logic
{
    /// <summary>触发插件事件回调并隔离异常：单个插件处理器抛异常不传播进核心事件泵。</summary>
    private void SafeRaise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "插件事件处理器执行失败: {0}", ex.Message);
        }
    }

    private void OnNoticeEventReceived(NapcatClient.EventType.NoticeEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseNoticeEventReceived(eventData));
    }

    private void OnGroupUploadEventReceived(NapcatClient.EventType.GroupUploadEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseGroupUploadEventReceived(eventData));
    }

    private void OnGroupAdminEventReceived(NapcatClient.EventType.GroupAdminEvent eventData)
    {
        messageService.RecordGroupAdmin(eventData);
        SafeRaise(() => EventRegister.RaiseGroupAdminEventReceived(eventData));
    }

    private void OnGroupDecreaseEventReceived(NapcatClient.EventType.GroupDecreaseEvent eventData)
    {
        messageService.RecordGroupDecrease(eventData);
        SafeRaise(() => EventRegister.RaiseGroupDecreaseEventReceived(eventData));
    }

    private void OnGroupIncreaseEventReceived(NapcatClient.EventType.GroupIncreaseEvent eventData)
    {
        messageService.RecordGroupIncrease(eventData);
        SafeRaise(() => EventRegister.RaiseGroupIncreaseEventReceived(eventData));
    }

    private void OnGroupBanEventReceived(NapcatClient.EventType.GroupBanEvent eventData)
    {
        messageService.RecordGroupBan(eventData);
        SafeRaise(() => EventRegister.RaiseGroupBanEventReceived(eventData));
    }

    private void OnFriendAddEventReceived(NapcatClient.EventType.FriendAddEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseFriendAddEventReceived(eventData));
    }

    private void OnGroupRecallEventReceived(NapcatClient.EventType.GroupRecallEvent eventData)
    {
        messageService.RecordGroupRecall(eventData);
        SafeRaise(() => EventRegister.RaiseGroupRecallEventReceived(eventData));
    }

    private void OnFriendRecallEventReceived(NapcatClient.EventType.FriendRecallEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseFriendRecallEventReceived(eventData));
    }

    private void OnPokeEventReceived(NapcatClient.EventType.PokeEvent eventData)
    {
        SafeRaise(() => EventRegister.RaisePokeEventReceived(eventData));
    }

    private void OnLuckyKingEventReceived(NapcatClient.EventType.LuckyKingEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseLuckyKingEventReceived(eventData));
    }

    private void OnHonorEventReceived(NapcatClient.EventType.HonorEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseHonorEventReceived(eventData));
    }

    private void OnGroupMsgEmojiLikeEventReceived(NapcatClient.EventType.GroupMsgEmojiLikeEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseGroupMsgEmojiLikeEventReceived(eventData));
    }

    private void OnEssenceEventReceived(NapcatClient.EventType.EssenceEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseEssenceEventReceived(eventData));
    }

    private void OnGroupCardEventReceived(NapcatClient.EventType.GroupCardEvent eventData)
    {
        SafeRaise(() => EventRegister.RaiseGroupCardEventReceived(eventData));
    }
}
