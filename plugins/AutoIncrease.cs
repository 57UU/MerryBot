using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("自动+1", "如果有刷屏消息，将会自动+1")]
public class AutoIncrease : Plugin
{
    public AutoIncrease(PluginInterop interop) : base(interop)
    {
    }
    //store each group
    Dictionary<long, ChainWithSender> lastMessage = new();
    Dictionary<long, ChainWithSender> lastIncreaseMessage = new();

    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        var _lastMessage = lastMessage.GetValueOrDefault(groupId);
        var chainList = PluginUtils.MessageSpan2List(chain);
        ChainWithSender chainWithSender = new()
        {
            chain=chainList,
            sender=data.sender.user_id
        };

        if (
            MessageUtils.IsEqual(chain, CollectionsMarshal.AsSpan(_lastMessage.chain)) 
            //&& _lastMessage.sender != data.sender.user_id//not by same account
            )
        {
            //this is a duplicated message
            var _lastIncreaseMessage = lastIncreaseMessage.GetValueOrDefault(groupId);
            if (!MessageUtils.IsEqual(_lastIncreaseMessage.chain, _lastMessage.chain))
            {
                //this has not been sent
                var msg = Actions.EmptyMessageChain;
                foreach (var entity in chain)
                {
                    msg.Add(entity);
                }
                Logger.Debug("+1 message detected");
                Actions.SendGroupMessage(groupId,msg);
                lastIncreaseMessage[groupId] = chainWithSender;
            }
        }
        lastMessage[groupId] = chainWithSender;

    }
}
internal struct ChainWithSender
{
    public List<Message>? chain=null;
    public long sender=0;
    public ChainWithSender() { }
}