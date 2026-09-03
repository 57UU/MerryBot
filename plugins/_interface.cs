using Agent.Session;
using CommonLib;
using DataProvider;
using NapcatClient;
using NapcatClient.Action;
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
        ObjectSaver pluginSaver, ObjectGetter pluginGetter, PluginDatabaseScope pluginDatabaseScope)
    {
        _pluginSaver = pluginSaver;
        _pluginGetter = pluginGetter;
        PluginDatabaseScope = pluginDatabaseScope;
    }

    private readonly ObjectSaver _pluginSaver;
    private readonly ObjectGetter _pluginGetter;
    public PluginDatabaseScope PluginDatabaseScope { get; private set; }

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

}

public delegate Task ObjectSaver(object data);
public delegate Task<object?> ObjectGetter();
public delegate Task ObjectGroupSaver(long groupId, object data);
public delegate Task<object?> ObjectGroupGetter(long groupId);
public delegate IEnumerable<PluginInfo> PluginInfoGetter();
/// <summary>
/// 拦截指定消息
/// </summary>
/// <param name="context"></param>
/// <returns>返回true拦截</returns>
public delegate bool MessageInterceptor(MessageContext context);

/// <summary>
/// 用于实现互操作性
/// </summary>
public record PluginInterop(
    ISimpleLogger Logger,
    IEnumerable<long> GroupId,
    PluginInfoGetter PluginInfoGetter,
    PluginStorage PluginStorage,
    IHostLifecycle Lifecycle,
    long AuthorizedUser,
    string PathPrefix,
    EventRegister EventRegister,
    IMessageService MessageService,
    MessageChannel Channel,
    ClockScope Clock
    )
{
    /// <summary>
    /// 注册拦截器，拦截器只会拦截当前插件的消息，不会拦截其他插件的消息
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

    /// <summary>将消息链转换为 Agent 使用的统一文本格式。</summary>
    public static string FormatMessageChain(IEnumerable<TypedMessage> messageChain)
        => string.Concat(messageChain.Select(FormatMessagePart));

    /// <summary>转换单个消息段，供引用展开和消息读取工具复用。</summary>
    internal static string FormatMessagePart(TypedMessage message)
        => message switch
        {
            TextData textData => textData.Text,
            AtData => string.Empty,
            ReplyData replyData => $"[引用消息 {replyData.Id}]",
            ForwardData forwardData => $"[转发消息 {forwardData.Id}]",
            FaceData faceData => $"[表情: {faceData.ToChinese()}]",
            MfaceData mfaceData => $"[商城表情: {mfaceData.Summary ?? mfaceData.EmojiId}]",
            DiceData diceData => $"[骰子: {diceData.Result}点]",
            RpsData rpsData => $"[猜拳: {rpsData.Result switch { "1" => "石头", "2" => "剪刀", _ => "布" }}]",
            PokeData => "[戳一戳]",
            ImageData imageData => $"[图片: {imageData.Summary ?? imageData.File}]",
            RecordData => "[语音]",
            VideoData videoData => $"[视频: {videoData.File}]",
            FileData fileData => $"[文件: {fileData.File}]",
            JsonData jsonData => $"[卡片消息: {jsonData.Data}]",
            MusicData musicData => $"[音乐: {musicData.Title ?? musicData.Id ?? musicData.Url}]",
            _ => message.ToString() ?? string.Empty,
        };

    /// <summary>
    /// 整消息统一信封：[时间]（已撤回）? [用户 id(昵称)][key=...]?: 内容。
    /// 消息读取工具与引用展开共用，已撤回消息保留原文并标记，不静默丢弃。
    /// </summary>
    internal static string FormatFullMessage(ProcessedMessage message, bool includeKey)
    {
        var timeStr = message.Time.ToString("yyyy-MM-dd HH:mm");
        var name = string.IsNullOrEmpty(message.SenderGroupNickname) ? message.SenderNickname : message.SenderGroupNickname;
        var content = FormatMessageChain(message.MessageChain);
        var isRecalled = message.IsDeleted ? "（已撤回）" : "";
        var key = includeKey ? $"[key={message.Id}]" : "";
        return $"[{timeStr}]{isRecalled} [用户 {message.SenderId}(昵称:{name})]{key}: {content}";
    }
}


public class PluginNotUsableException : Exception
{
    public PluginNotUsableException(string message) : base(message)
    {
    }
}


public record Command(string Name, ImmutableArray<string> Args);


public interface IPluginConfig { };
