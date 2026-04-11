using NapcatClient.EventType;
using NapcatClient.MessageType;

namespace NapcatClient;
//message
public delegate void GroupMessageCallback(long groupId, List<TypedMessage> messageChain, ReceivedGroupMessage data);

//event
public delegate void NoticeEventCallback(NoticeEvent noticeEvent);
public delegate void GroupUploadEventCallback(GroupUploadEvent eventData);
public delegate void GroupAdminEventCallback(GroupAdminEvent eventData);
public delegate void GroupDecreaseEventCallback(GroupDecreaseEvent eventData);
public delegate void GroupIncreaseEventCallback(GroupIncreaseEvent eventData);
public delegate void GroupBanEventCallback(GroupBanEvent eventData);
public delegate void FriendAddEventCallback(FriendAddEvent eventData);
public delegate void GroupRecallEventCallback(GroupRecallEvent eventData);
public delegate void FriendRecallEventCallback(FriendRecallEvent eventData);
public delegate void PokeEventCallback(PokeEvent eventData);
public delegate void LuckyKingEventCallback(LuckyKingEvent eventData);
public delegate void HonorEventCallback(HonorEvent eventData);
public delegate void GroupMsgEmojiLikeEventCallback(GroupMsgEmojiLikeEvent eventData);
public delegate void EssenceEventCallback(EssenceEvent eventData);
public delegate void GroupCardEventCallback(GroupCardEvent eventData);
