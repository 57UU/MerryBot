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
    const int REPEAT_TIME = 3;
    public AutoIncrease(PluginInterop interop) : base(interop)
    {
        interop.Interceptors.Add((data) => {
            return data.sender.user_id == interop.BotClient.SelfId;
        });
    }
    //store each group
    Dictionary<long, ChainWithSender> lastMessage = new();

    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        var _lastMessage = lastMessage.GetValueOrDefault(groupId);
        
        if (_lastMessage == null)
        {
            var chainList = PluginUtils.MessageSpan2List(chain);
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
                MessageUtils.IsEqual(chain, CollectionsMarshal.AsSpan(_lastMessage?.chain))
                //&& _lastMessage.sender != data.sender.user_id//not by same account
                )
            {
                //this is a duplicated message
                _lastMessage!.repeatTime++;

                if (!_lastMessage.used && _lastMessage.repeatTime >= REPEAT_TIME)
                {
                    //this has not been sent
                    var msg = NapcatClient.Action.Actions.EmptyMessageChain;
                    foreach (var entity in chain)
                    {
                        msg.Add(entity);
                    }
                    Logger.Info("+1 message detected");
                    Actions.SendGroupMessage(groupId, msg);
                    _lastMessage.used = true;
                }
            }
            else
            {
                //不是重复消息
                var chainList = PluginUtils.MessageSpan2List(chain);
                _lastMessage!.Renew(chainList, data.sender.user_id);

            }
        }
    }
}
internal class ChainWithSender
{
    public List<Message>? chain=null;
    public long sender=0;
    public int repeatTime = 1;
    public bool used = false;
    public ChainWithSender() { }
    public void Renew(List<Message> chain,long sender)
    {
        this.chain = chain;
        repeatTime = 1;
        used = false;
        this.sender = sender;
    }
}