using NapcatClient;
using NapcatClient.MessageType;

namespace BotPlugin;

[PluginTag("auto-increase", "自动+1", "如果有刷屏消息，将会自动+1", type: PluginType.Background)]
public class AutoIncrease : Plugin
{
    private readonly AutoIncreaseConfig config;
    /// <summary>跟踪群数的上限，防止字典无限增长</summary>
    private const int MaxTrackedGroups = 500;
    public AutoIncrease(PluginInterop interop, AutoIncreaseConfig config) : base(interop)
    {
        this.config = config;
        //配置校验：重复次数至少为 2，避免出现 1 次即触发
        if (config.RepeatTime < 2)
        {
            config.RepeatTime = 2;
        }
        interop.Interceptors.Add((ctx) => ctx.SenderId == ctx.SelfId);
    }
    //store each group
    private readonly Dictionary<long, ChainWithSender> lastMessage = [];
    private readonly object lastMessageLock = new();

    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, MessageContext context)
    {
        lock (lastMessageLock)
        {
            var groupId = long.Parse(context.Session.Id);
            var _lastMessage = lastMessage.GetValueOrDefault(groupId);
            //群已不在监听列表时，移除该群的过期状态，避免残留计数
            if (_lastMessage != null && !Interop.GroupId.Contains(groupId))
            {
                lastMessage.Remove(groupId);
                _lastMessage = null;
            }
            var chainList = messageChain.Select(message => message.Clone()).ToList();
            if (_lastMessage == null)
            {
                _lastMessage = new()
                {
                    chain = chainList,
                    sender = context.SenderId
                };
                lastMessage[groupId] = _lastMessage;
                //上限保护：超过上限时移除最早跟踪的群
                if (lastMessage.Count > MaxTrackedGroups)
                {
                    lastMessage.Remove(lastMessage.Keys.First());
                }
            }
            else
            {
                if (MessageUtils.IsEqual(messageChain, _lastMessage.chain))
                {
                    _lastMessage.repeatTime++;
                    if (!_lastMessage.used && _lastMessage.repeatTime >= config.RepeatTime)
                    {
                        Logger.Info("+1 message detected");
                        _ = Channel.SendMessage(context.Session, _lastMessage.chain!);
                        _lastMessage.used = true;
                    }
                }
                else
                {
                    _lastMessage.Renew(chainList, context.SenderId);
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
