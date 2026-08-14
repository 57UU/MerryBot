

namespace MerryBot;

internal partial class Logic
{
    private void OnNoticeEventReceived(NapcatClient.EventType.NoticeEvent eventData)
    {
        EventRegister.RaiseNoticeEventReceived(eventData);
    }

    private void OnGroupUploadEventReceived(NapcatClient.EventType.GroupUploadEvent eventData)
    {
        EventRegister.RaiseGroupUploadEventReceived(eventData);
    }

    private void OnGroupAdminEventReceived(NapcatClient.EventType.GroupAdminEvent eventData)
    {
        messageService.RecordGroupAdmin(eventData);
        EventRegister.RaiseGroupAdminEventReceived(eventData);
    }

    private void OnGroupDecreaseEventReceived(NapcatClient.EventType.GroupDecreaseEvent eventData)
    {
        messageService.RecordGroupDecrease(eventData);
        EventRegister.RaiseGroupDecreaseEventReceived(eventData);
    }

    private void OnGroupIncreaseEventReceived(NapcatClient.EventType.GroupIncreaseEvent eventData)
    {
        messageService.RecordGroupIncrease(eventData);
        EventRegister.RaiseGroupIncreaseEventReceived(eventData);
    }

    private void OnGroupBanEventReceived(NapcatClient.EventType.GroupBanEvent eventData)
    {
        messageService.RecordGroupBan(eventData);
        EventRegister.RaiseGroupBanEventReceived(eventData);
    }

    private void OnFriendAddEventReceived(NapcatClient.EventType.FriendAddEvent eventData)
    {
        EventRegister.RaiseFriendAddEventReceived(eventData);
    }

    private void OnGroupRecallEventReceived(NapcatClient.EventType.GroupRecallEvent eventData)
    {
        messageService.RecordGroupRecall(eventData);
        EventRegister.RaiseGroupRecallEventReceived(eventData);
    }

    private void OnFriendRecallEventReceived(NapcatClient.EventType.FriendRecallEvent eventData)
    {
        EventRegister.RaiseFriendRecallEventReceived(eventData);
    }

    private void OnPokeEventReceived(NapcatClient.EventType.PokeEvent eventData)
    {
        EventRegister.RaisePokeEventReceived(eventData);
    }

    private void OnLuckyKingEventReceived(NapcatClient.EventType.LuckyKingEvent eventData)
    {
        EventRegister.RaiseLuckyKingEventReceived(eventData);
    }

    private void OnHonorEventReceived(NapcatClient.EventType.HonorEvent eventData)
    {
        EventRegister.RaiseHonorEventReceived(eventData);
    }

    private void OnGroupMsgEmojiLikeEventReceived(NapcatClient.EventType.GroupMsgEmojiLikeEvent eventData)
    {
        EventRegister.RaiseGroupMsgEmojiLikeEventReceived(eventData);
    }

    private void OnEssenceEventReceived(NapcatClient.EventType.EssenceEvent eventData)
    {
        EventRegister.RaiseEssenceEventReceived(eventData);
    }

    private void OnGroupCardEventReceived(NapcatClient.EventType.GroupCardEvent eventData)
    {
        EventRegister.RaiseGroupCardEventReceived(eventData);
    }
}
