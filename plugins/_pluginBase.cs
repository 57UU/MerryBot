using CommonLib;
using NapcatClient;
using NapcatClient.Action;
using NapcatClient.EventType;
using NapcatClient.MessageType;

namespace BotPlugin;

/// <summary>
/// 插件的基类，所有插件必须继承此类，实现了基本的方法
/// </summary>
public abstract class Plugin : IDisposable
{
    /// <summary>
    /// 动作类，用于发送消息等
    /// </summary>
    protected Actions Actions { get; set; }
    /// <summary>
    /// 当为假时，OnMessageReceived函数永远不会被调用
    /// </summary>
    public bool IsEnable { get; internal set; } = true;
    /// <summary>
    /// 当前工作范围，在哪些QQ群工作
    /// </summary>
    protected readonly IEnumerable<long> GroupId;
    /// <summary>
    /// 日志记录器
    /// </summary>
    protected readonly ISimpleLogger Logger;
    /// <summary>
    /// 插件设置，包括主程序互操作性内容
    /// </summary>
    protected readonly PluginInterop Interop;
    /// <summary>
    /// 初始化插件设置，设置互操作性
    /// </summary>
    /// <param name="interop">互操作性</param>
    public Plugin(PluginInterop interop)
    {

        this.Logger = interop.Logger;
        this.GroupId = interop.GroupId;
        this.Interop = interop;
        Actions = interop.BotClient.Actions;
    }
    /// <summary>
    /// 检测消息链是否以prefix开头
    /// </summary>
    /// <param name="chain">消息链</param>
    /// <param name="prefix">前缀</param>
    /// <returns></returns>
    public static bool IsStartsWith(MessageChain chain, string prefix)
    {
        if (chain.Length >= 1 && chain[0] is TextData textData)
        {
            string text = textData.Text;
            text = text.Trim();
            if (text.StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }
    /// <summary>
    /// 检测消息链是否以prefix开头
    /// </summary>
    /// <param name="chain">消息链</param>
    /// <param name="prefix">前缀</param>
    /// <returns></returns>
    public static bool IsStartsWith(IEnumerable<TypedMessage> chain, string prefix)
    {
        var first = chain.FirstOrDefault();
        if (first != null && first is TextData textData)
        {
            string text = textData.Text;
            text = text.Trim();
            if (text.StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }
    /// <summary>
    /// 当有新消息来时，此方法会被调用
    /// </summary>
    /// <param name="chain">接收到的消息链</param>
    /// <param name="groupId">对应的QQ群号</param>
    /// <param name="data">总数据</param>
    public virtual void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {

    }
    public virtual void OnGroupMessageNotMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {

    }
    public virtual void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {

    }

    /// <summary>
    /// 当收到通知事件时调用
    /// </summary>
    /// <param name="eventData">通知事件数据</param>
    public virtual void OnNoticeEvent(NoticeEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群文件上传事件时调用
    /// </summary>
    /// <param name="eventData">群文件上传事件数据</param>
    public virtual void OnGroupUploadEvent(GroupUploadEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群管理员变动事件时调用
    /// </summary>
    /// <param name="eventData">群管理员变动事件数据</param>
    public virtual void OnGroupAdminEvent(GroupAdminEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群成员减少事件时调用
    /// </summary>
    /// <param name="eventData">群成员减少事件数据</param>
    public virtual void OnGroupDecreaseEvent(GroupDecreaseEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群成员增加事件时调用
    /// </summary>
    /// <param name="eventData">群成员增加事件数据</param>
    public virtual void OnGroupIncreaseEvent(GroupIncreaseEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群禁言事件时调用
    /// </summary>
    /// <param name="eventData">群禁言事件数据</param>
    public virtual void OnGroupBanEvent(GroupBanEvent eventData)
    {

    }

    /// <summary>
    /// 当收到新添加好友事件时调用
    /// </summary>
    /// <param name="eventData">新添加好友事件数据</param>
    public virtual void OnFriendAddEvent(FriendAddEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群消息撤回事件时调用
    /// </summary>
    /// <param name="eventData">群消息撤回事件数据</param>
    public virtual void OnGroupRecallEvent(GroupRecallEvent eventData)
    {

    }

    /// <summary>
    /// 当收到好友消息撤回事件时调用
    /// </summary>
    /// <param name="eventData">好友消息撤回事件数据</param>
    public virtual void OnFriendRecallEvent(FriendRecallEvent eventData)
    {

    }

    /// <summary>
    /// 当收到戳一戳事件时调用
    /// </summary>
    /// <param name="eventData">戳一戳事件数据</param>
    public virtual void OnPokeEvent(PokeEvent eventData)
    {

    }

    /// <summary>
    /// 当收到运气王事件时调用
    /// </summary>
    /// <param name="eventData">运气王事件数据</param>
    public virtual void OnLuckyKingEvent(LuckyKingEvent eventData)
    {

    }

    /// <summary>
    /// 当收到荣誉变更事件时调用
    /// </summary>
    /// <param name="eventData">荣誉变更事件数据</param>
    public virtual void OnHonorEvent(HonorEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群表情回应事件时调用
    /// </summary>
    /// <param name="eventData">群表情回应事件数据</param>
    public virtual void OnGroupMsgEmojiLikeEvent(GroupMsgEmojiLikeEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群精华事件时调用
    /// </summary>
    /// <param name="eventData">群精华事件数据</param>
    public virtual void OnEssenceEvent(EssenceEvent eventData)
    {

    }

    /// <summary>
    /// 当收到群名片变更事件时调用
    /// </summary>
    /// <param name="eventData">群名片变更事件数据</param>
    public virtual void OnGroupCardEvent(GroupCardEvent eventData)
    {

    }

    public virtual Task OnLoaded()
    {
        return Task.CompletedTask;
    }

    public virtual void Dispose()
    {

    }
}