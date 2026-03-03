

namespace MerryBot;

internal partial class Logic
{
    private void OnNoticeEventReceived(NapcatClient.EventType.NoticeEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnNoticeEvent(eventData);
        }
    }

    private void OnGroupUploadEventReceived(NapcatClient.EventType.GroupUploadEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupUploadEvent(eventData);
        }
    }

    private void OnGroupAdminEventReceived(NapcatClient.EventType.GroupAdminEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupAdminEvent(eventData);
        }
    }

    private void OnGroupDecreaseEventReceived(NapcatClient.EventType.GroupDecreaseEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupDecreaseEvent(eventData);
        }
    }

    private void OnGroupIncreaseEventReceived(NapcatClient.EventType.GroupIncreaseEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupIncreaseEvent(eventData);
        }
    }

    private void OnGroupBanEventReceived(NapcatClient.EventType.GroupBanEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupBanEvent(eventData);
        }
    }

    private void OnFriendAddEventReceived(NapcatClient.EventType.FriendAddEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnFriendAddEvent(eventData);
        }
    }

    private void OnGroupRecallEventReceived(NapcatClient.EventType.GroupRecallEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupRecallEvent(eventData);
        }
    }

    private void OnFriendRecallEventReceived(NapcatClient.EventType.FriendRecallEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnFriendRecallEvent(eventData);
        }
    }

    private void OnPokeEventReceived(NapcatClient.EventType.PokeEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnPokeEvent(eventData);
        }
    }

    private void OnLuckyKingEventReceived(NapcatClient.EventType.LuckyKingEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnLuckyKingEvent(eventData);
        }
    }

    private void OnHonorEventReceived(NapcatClient.EventType.HonorEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnHonorEvent(eventData);
        }
    }

    private void OnGroupMsgEmojiLikeEventReceived(NapcatClient.EventType.GroupMsgEmojiLikeEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupMsgEmojiLikeEvent(eventData);
        }
    }

    private void OnEssenceEventReceived(NapcatClient.EventType.EssenceEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnEssenceEvent(eventData);
        }
    }

    private void OnGroupCardEventReceived(NapcatClient.EventType.GroupCardEvent eventData)
    {
        foreach (var plugin in plugins)
        {
            if (!plugin.Instance.IsEnable) continue;
            plugin.Instance.OnGroupCardEvent(eventData);
        }
    }
}