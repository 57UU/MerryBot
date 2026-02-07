using NapcatClient.MessageType;

namespace NapcatClient;
public delegate void GroupMessageCallback(long groupId, List<TypedMessage> messageChain, ReceivedGroupMessage data);
