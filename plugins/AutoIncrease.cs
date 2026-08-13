using NapcatClient;
using NapcatClient.MessageType;
using System.Runtime.InteropServices;

namespace BotPlugin;

[PluginTag("auto-increase", "自动+1", "如果有刷屏消息，将会自动+1", priority: -1, type: PluginType.Background)]
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

    public override void OnGroupMessage(bool isMentioned, Command? command, ReceivedGroupMessage data)
    {
        var groupId = data.GroupId;
        var _lastMessage = lastMessage.GetValueOrDefault(groupId);
        var chainList = data.message;
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
            //_lastMessage is not null
            //上一个消息存在
            if (
                MessageUtils.IsEqual(CollectionsMarshal.AsSpan(data.message), CollectionsMarshal.AsSpan(_lastMessage?.chain))
                //&& _lastMessage.sender != data.sender.user_id//not by same account
                )
            {
                //this is a duplicated message
                _lastMessage!.repeatTime++;

                if (!_lastMessage.used && _lastMessage.repeatTime >= REPEAT_TIME)
                {
                    //this has not been sent
                    Logger.Info("+1 message detected");
                    _ = Bot.SendGroupMessage(groupId, _lastMessage.chain!);
                    _lastMessage.used = true;
                }
            }
            else
            {
                //不是重复消息
                _lastMessage!.Renew(chainList, data.sender.user_id);

            }
        }
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