using NapcatClient;
using NapcatClient.MessageType;

namespace BotPlugin;

[PluginTag("auto-increase", "自动+1", "如果有刷屏消息，将会自动+1", type: PluginType.Background)]
public class AutoIncrease : Plugin
{
    const int REPEAT_TIME = 3;
    public AutoIncrease(PluginInterop interop) : base(interop)
    {
        var selfId = interop.BotClient.SelfId;
        interop.Interceptors.Add((data) =>
        {
            return data.sender.user_id == selfId;
        });
    }
    //store each group
    private readonly Dictionary<long, ChainWithSender> lastMessage = [];
    private readonly object lastMessageLock = new();

    public override Task OnGroupMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, ReceivedGroupMessage data)
    {
        lock (lastMessageLock)
        {
            var groupId = data.GroupId;
            var _lastMessage = lastMessage.GetValueOrDefault(groupId);
            var chainList = messageChain.Select(message => message.Clone()).ToList();
            if (_lastMessage == null)
            {
                _lastMessage = new()
                {
                    chain = chainList,
                    sender = data.sender.user_id
                };
                lastMessage[groupId] = _lastMessage;
            }
            else
            {
                if (MessageUtils.IsEqual(messageChain, _lastMessage.chain))
                {
                    _lastMessage.repeatTime++;
                    if (!_lastMessage.used && _lastMessage.repeatTime >= REPEAT_TIME)
                    {
                        Logger.Info("+1 message detected");
                        _ = Bot.SendGroupMessage(groupId, _lastMessage.chain!);
                        _lastMessage.used = true;
                    }
                }
                else
                {
                    _lastMessage.Renew(chainList, data.sender.user_id);
                }

            }
        }
        return Task.CompletedTask;
    }
}
internal class ChainWithSender
{
    public List<TypedMessage>? chain = null;
    public long sender = 0;
    public int repeatTime = 1;
    public bool used = false;
    public ChainWithSender() { }
    public void Renew(List<TypedMessage> chain, long sender)
    {
        this.chain = chain;
        repeatTime = 1;
        used = false;
        this.sender = sender;
    }
}
