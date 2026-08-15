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
    protected Actions Bot { get; set; }
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
        Bot = interop.Bot;
    }
    public virtual Task OnGroupMessageAsync(
        bool isMentioned,
        Command? command,
        IReadOnlyList<TypedMessage> messageChain,
        ReceivedGroupMessage raw) => Task.CompletedTask;

    public virtual Task OnLoaded()
    {
        return Task.CompletedTask;
    }

    public virtual void Dispose()
    {

    }
}
