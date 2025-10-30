using CommonLib;
using NapcatClient;
using NapcatClient.Action;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

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
        if (chain.Length >= 1 && chain[0].MessageType == "text")
        {
            string text = chain[0].Data["text"];
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
    public static bool IsStartsWith(IEnumerable<Message> chain, string prefix)
    {
        var first= chain.FirstOrDefault();
        if (first!=null && first.MessageType == "text")
        {
            string text = first.Data["text"];
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
    public virtual Task OnLoaded()
    {
        return Task.CompletedTask;
    }

    private static JsonSerializerOptions _options = new JsonSerializerOptions() { IncludeFields = true };
    /// <summary>
    /// 存储某个对象
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    /// <returns></returns>
    protected Task SaveData<T>(T data)
    {
        var json = JsonSerializer.Serialize(data, _options);
        return Interop.PluginStorage.Saver.Invoke(json);
    }
    /// <summary>
    /// 加载存储的对象
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    protected async Task<T> LoadData<T>()
    {
        var json = await Interop.PluginStorage.Getter.Invoke();
        if (json == string.Empty)
        {
            json = "{}";
        }
        var data = JsonSerializer.Deserialize<T>(json, _options)!;
        return data;
    }

    public virtual void Dispose()
    {
        
    }
}