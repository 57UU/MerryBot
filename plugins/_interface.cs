global using Detail = System.Collections.Generic.Dictionary<string, dynamic>;
global using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;
using CommonLib;
using NapcatClient;
using NapcatClient.Action;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;

namespace BotPlugin;



public delegate Plugin PluginBuilder(PluginInterop config);

/// <summary>
/// 插件的完整信息
/// </summary>
/// <param name="Instance"></param>
/// <param name="PluginTag"></param>
/// <param name="Interop"></param>
public record PluginInfo(
    Plugin Instance,
    PluginTag PluginTag,
    PluginInterop Interop
    );
/// <summary>
/// 插件存储，建议使用其内置的json方法进行存储
/// </summary>
/// <param name="Saver"></param>
/// <param name="Getter"></param>
public record PluginStorage(StringSaver Saver,StringGetter Getter)
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        IncludeFields = true,
    };
    public async Task<T> Load<T>(T defaultValue) where T : class
    {
        var data = await Getter();
        return JsonSerializer.Deserialize<T>(data, _jsonSerializerOptions) ?? defaultValue;
    }
    public async Task Save<T>(T data) where T : class
    {
        var json = JsonSerializer.Serialize(data, _jsonSerializerOptions);
        await Saver(json);
    }
}
public delegate Task StringSaver(string data);
public delegate Task<string> StringGetter();
public delegate IEnumerable<PluginInfo> PluginInfoGetter();
/// <summary>
/// 拦截指定消息
/// </summary>
/// <param name="data"></param>
/// <returns>返回true拦截</returns>
public delegate bool MessageInterceptor(ReceivedGroupMessage data);

/// <summary>
/// 用于实现互操作性
/// </summary>
public record PluginInterop(
    ISimpleLogger Logger,
    IEnumerable<long> GroupId,
    PluginInfoGetter PluginInfoGetter,
    PluginStorage PluginStorage,
    BotClient BotClient,
    Detail Variables,
    Action<int> Shutdown,
    long AuthorizedUser,
    string[] CommandLineArguments
    )
{
    /// <summary>
    /// 注册拦截器
    /// </summary>
    public List<MessageInterceptor> Interceptors { get; } = new();
    /// <summary>
    /// find the plugin of specific type
    /// </summary>
    /// <typeparam name="T">插件的类型</typeparam>
    /// <returns>如果找得到，返回该插件的实例，否则返回null</returns>
    internal T? FindPlugin<T>() where T : Plugin
    {
        return this.PluginInfoGetter().FirstOrDefault(i => i.Instance is T)?.Instance as T;
    }
    /// <summary>
    /// 尝试在配置文件的变量中查找
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    internal T GetVariable<T>(string key,T defaultValue)
    {
        Variables.TryGetValue(key, out var value);
        if (value is null)
        {
            return defaultValue;
        }
        var realValue=(JsonElement)value ;
        return realValue.Deserialize<T>()!;
    }
    internal JsonElement? GetJsonElement(string key)
    {
        Variables.TryGetValue(key, out var value);
        if (value is null)
        {
            return null;
        }
        return (JsonElement)value;
    }
    internal Nullable<long> GetLongVariable(string key)
    {
        Variables.TryGetValue(key, out var value);
        if (value is null)
        {
            return null;
        }
        var realValue = (JsonElement)value;
        return realValue.Deserialize<long>()!;
    }
    /// <summary>
    /// 在配置文件的变量中查找
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <returns></returns>
    internal T? GetVariable<T>(string key) where T : class
    {
        Variables.TryGetValue(key, out var value);
        if (value is null)
        {
            return null;
        }
        var realValue = (JsonElement)value;
        return realValue.Deserialize<T>();
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
    /// 插件的优先级，决定加载顺序。值越小，优先级越高
    /// </summary>
    public readonly int Priority;
    /// <summary>
    /// 插件的tag，用于标记插件
    /// </summary>
    /// <param name="name">名称</param>
    /// <param name="description">描述</param>
    /// <param name="isIgnore">加载插件时是否忽略这个插件</param>
    public PluginTag(string name, string description, bool isIgnore=false, int priority=0)
    {
        Name = name;
        Description = description;
        IsIgnore = isIgnore;
        Priority = priority;
    }
}
/// <summary>
/// 消息链工具类
/// </summary>
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
        if (a.IsEmpty || b.IsEmpty) { return false; }
        var a1 = a.ToArray();
        var b1 = b.ToArray();
        if (a1.Length != b1.Length)
        {
            return false;
        }
        for (var i = 0; i < a1.Length; i++)
        {
            var o1=a1[i];
            var o2=b1[i];
            if(o1==null || o2 == null)
            {
                return false;
            }
            if (!o1.Equals(o2))
            {
                return false;
            }
        }
        return true;
    }
    public static bool IsEqual(List<Message>? a, List<Message>? b)
    {
        return IsEqual(
            CollectionsMarshal.AsSpan(a),
            CollectionsMarshal.AsSpan(b)
            );
    }
}


public class PluginNotUsableException : Exception
{
    public PluginNotUsableException(string message) : base(message)
    {
    }
}