global using Detail = System.Collections.Generic.Dictionary<string, dynamic>;
global using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;
using NapcatClient;
using NapcatClient.Action;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotPlugin;



public delegate Plugin PluginBuilder(PluginInterop config);

public record PluginInfo(
    Plugin Instance,
    PluginTag PluginTag,
    PluginInterop Interop
    );

public record PluginStorage(StringSaver Saver,StringGetter Getter);
public delegate Task StringSaver(string data);
public delegate Task<string> StringGetter();
public delegate IEnumerable<PluginInfo> PluginInfoGetter();

/// <summary>
/// 用于实现互操作性
/// </summary>
public record PluginInterop(
    ISimpleLogger Logger,
    IEnumerable<long> GroupId,
    PluginInfoGetter PluginInfoGetter,
    PluginStorage PluginStorage,
    BotClient BotClient,
    Detail Variables
    )
{
    /// <summary>
    /// find the plugin of specific type
    /// </summary>
    /// <typeparam name="T">插件的类型</typeparam>
    /// <returns>如果找得到，返回该插件的实例，否则返回null</returns>
    internal T? FindPlugin<T>() where T : Plugin
    {
        return this.PluginInfoGetter().First(i => i.Instance is T).Instance as T;
    }
    internal T GetVariable<T>(string key,T defaultValue)
    {
        Variables.TryGetValue(key, out var value);
        if (value is null)
        {
            return defaultValue;
        }
        var realValue=(JsonElement)value ;
        return realValue.Deserialize<T>();
    }
    internal T? GetVariable<T>(string key)
    {
        Variables.TryGetValue(key, out var value);
        return value;
    }
}
public abstract class Plugin
{
    protected Actions Actions;
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
    /// 当有新消息来时，此方法会被调用
    /// </summary>
    /// <param name="chain">接收到的消息链</param>
    /// <param name="groupId">对应的QQ群号</param>
    /// <param name="data">总数据</param>
    public virtual void OnGroupMessageMentioned(long groupId, MessageChain chain, Detail data)
    {

    }
    public virtual void OnGroupMessageNotMentioned(long groupId, MessageChain chain, Detail data)
    {

    }
    public virtual void OnGroupMessage(long groupId, MessageChain chain, Detail data)
    {

    }
    public virtual void OnLoaded()
    {

    }

    private JsonSerializerOptions _options= new JsonSerializerOptions() { IncludeFields = true };
    /// <summary>
    /// 存储某个对象
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    /// <returns></returns>
    protected Task SaveData<T>(T data)
    {
        var json=JsonSerializer.Serialize(data, _options);
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
        var data=JsonSerializer.Deserialize<T>(json, _options)!;
        return data;
    }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class PluginTag : Attribute
{
    public string Name { get; private set; }
    public string Description {  get; private set; }
    /// <summary>
    /// 当为真时，加载插件时将会忽略这个插件。
    /// </summary>
    public bool IsIgnore { get; private set; }
    /// <summary>
    /// 插件的tag，用于标记插件
    /// </summary>
    /// <param name="name">名称</param>
    /// <param name="description">描述</param>
    /// <param name="isIgnore">加载插件时是否忽略这个插件</param>
    public PluginTag(string name, string description, bool isIgnore=false)
    {
        Name = name;
        Description = description;
        IsIgnore = isIgnore;
    }
}

public static class MessageUtils
{
    /// <summary>
    /// 比较两个MessageChain是否相等，只看内容，忽略发送者
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static bool IsEqual(MessageChain a,MessageChain b)
    {
        if (a == null || b == null) { return false; }
        var a1 = a.ToArray();
        var b1 = b.ToArray();
        if (a1.Length != b1.Length)
        {
            return false;
        }
        for (var i = 0; i < a1.Length; i++)
        {
            if (a1[i].ToPreviewText() != b1[i].ToPreviewText())
            {
                return false;
            }
            return false;
        }
        return true;
    }
}