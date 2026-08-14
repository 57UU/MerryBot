using CommonLib;
using DataProvider;
using Microsoft.AspNetCore.Builder;
using NapcatClient;
using NapcatClient.MessageType;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

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
/// 插件存储，支持异步读取和写入
/// </summary>
public class PluginStorage
{
    public PluginStorage(
        ObjectSaver pluginSaver, ObjectGetter pluginGetter,
        ObjectGroupSaver groupSaver, ObjectGroupGetter groupGetter)
    {
        _pluginSaver = pluginSaver;
        _pluginGetter = pluginGetter;
        _groupSaver = groupSaver;
        _groupGetter = groupGetter;
    }

    private readonly ObjectSaver _pluginSaver;
    private readonly ObjectGetter _pluginGetter;
    private readonly ObjectGroupSaver _groupSaver;
    private readonly ObjectGroupGetter _groupGetter;

    public async Task<T?> Load<T>() where T : class
    {
        var data = await _pluginGetter();
        if (data is null) return null;
        return (T)data;
    }

    public async Task<T> Load<T>(T defaultValue) where T : class
    {
        var data = await _pluginGetter();
        if (data is null) return defaultValue;
        return (T)data;
    }

    public async Task Save<T>(T data) where T : class
        => await _pluginSaver(data);

    public async Task<T?> LoadGroup<T>(long groupId) where T : class
    {
        var data = await _groupGetter(groupId);
        if (data is null) return null;
        return (T)data;
    }

    public async Task SaveGroup<T>(long groupId, T data) where T : class
        => await _groupSaver(groupId, data);
}

public delegate Task ObjectSaver(object data);
public delegate Task<object?> ObjectGetter();
public delegate Task ObjectGroupSaver(long groupId, object data);
public delegate Task<object?> ObjectGroupGetter(long groupId);
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
    PluginDatabaseScope PluginDatabase,
    BotClient BotClient,
    IDictionary<string, object> Variables,
    Action<int> Shutdown,
    long AuthorizedUser,
    string[] CommandLineArguments,
    Func<Task> ConfigSaver,
    string PathPrefix,
    EventRegister EventRegister,
    IMessageService MessageService,
    WebApplication WebApplication
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
    /// 尝试在配置文件的变量中查找，如果没有找到，那就存储并返回默认值。出于性能考量，保存会异步执行。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    internal T GetVariableOrSetDefault<T>(string key, T defaultValue)
    {
        if (!Variables.TryGetValue(key, out var value))
        {
            //save it
            SetVariable(key, defaultValue);
            _ = SaveConfig();
            return defaultValue;
        }
        return (T)value;
    }
    internal int GetIntVariableOrSetDefault(string key, int defaultValue)
    {
        if (!Variables.TryGetValue(key, out var value))
        {
            //save it
            SetVariable(key, defaultValue);
            _ = SaveConfig();
            return defaultValue;
        }
        return Convert.ToInt32(value);
    }
    /// <summary>
    /// try get config value
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    internal bool TryGetVariable<T>(string key, out T? value)
    {
        if (!Variables.TryGetValue(key, out var rawValue) || rawValue == null)
        {
            value = default(T?);
            return false;
        }
        value = (T?)rawValue;
        return true;
    }
    internal T? GetStructVariable<T>(string key) where T : struct
    {
        if (!Variables.TryGetValue(key, out var value))
        {
            return default;
        }
        return (T)Convert.ChangeType(value, typeof(T));
    }
    internal T GetStructVariableOrSetDefault<T>(string key, T defaultValue) where T : struct
    {
        if (!Variables.TryGetValue(key, out var value))
        {
            //save it
            SetVariable(key, defaultValue);
            _ = SaveConfig();
            return defaultValue;
        }
        return (T)Convert.ChangeType(value, typeof(T));
    }
    internal T? GetClassVariable<T>(string key) where T : class
    {
        if (!Variables.TryGetValue(key, out var value))
        {
            return default;
        }
        return (T)value;
    }

    internal void SetVariable<T>(string key, T value)
    {
        Variables[key] = value!;
    }
    internal async Task SaveConfig()
    {
        await ConfigSaver.Invoke();
    }
}
public enum PluginType
{
    Interactive, Background, Admin
}
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class PluginTag : Attribute
{
    public readonly string Id;
    public readonly string Name;
    public readonly string Description;
    /// <summary>
    /// 当为真时，加载插件时将会忽略这个插件。
    /// </summary>
    public readonly bool IsIgnore;
    public readonly PluginType Type;
    /// <summary>
    /// 插件的tag，用于标记插件
    /// </summary>
    /// <param name="id">标识ID</param>
    /// <param name="name">名称</param>
    /// <param name="description">描述</param>
    /// <param name="isIgnore">加载插件时是否忽略这个插件</param>
    public PluginTag(string id, string name, string description, bool isIgnore = false, PluginType type = PluginType.Interactive)
    {
        Id = id;
        Name = name;
        Description = description;
        IsIgnore = isIgnore;
        Type = type;
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
    public static bool IsEqual(IReadOnlyList<TypedMessage>? a, IReadOnlyList<TypedMessage>? b)
    {
        if (a == null || b == null || a.Count == 0 || b.Count == 0) { return false; }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            var o1 = a[i];
            var o2 = b[i];
            if (o1 == null || o2 == null)
            {
                return false;
            }
            if (o1.GetType() != o2.GetType())
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
}


public class PluginNotUsableException : Exception
{
    public PluginNotUsableException(string message) : base(message)
    {
    }
}


public record Command(string Name, ImmutableArray<string> Args);
