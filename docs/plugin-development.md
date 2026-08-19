---
title: 插件开发
nav_order: 3
---

编写一个插件需要满足以下条件：

1. 一个插件应当放在 `plugins` 项目的一个文件中
2. 应当继承于 `Plugin` 抽象类
3. 有且只有一个构造函数，存在类型为 `PluginInterop` 的参数；插件之间不再依赖消息记录器或存储管理器
4. 在类前面使用属性 `PluginTag(string id, string name, string description, [bool isIgnore=false], [PluginType type=PluginType.Interactive])`

主程序会通过反射加载 `plugins` 项目下的所有插件类，因此需要满足上述条件。

## 示例

插件通过构造函数接收 `PluginInterop`；消息、资源和历史记录均由 Core 提供：

```csharp
[PluginTag("about", "About", "使用 /about 来查看关于")]
public class About : Plugin
{
    private const string aboutMessage=
"""
# -------About-------

Merry Bot

本程序的目的是实现QQ机器人的模块化开发，以插件的形式增加功能

访问Github仓库 https://github.com/57UU/MerryBot 以获取更多信息
""";

    public About(PluginInterop interop) : base(interop)
    {
        Logger.Info("about plugin start");
    }
    public override Task OnMessageAsync(
        bool isMentioned,
        Command? command,
        IReadOnlyList<TypedMessage> messageChain,
        MessageContext context)
    {
        if (isMentioned && command?.Name == "about")
        {
            _ = Channel.SendMessage(context.Session, aboutMessage);
        }
        return Task.CompletedTask;
    }
}
```

更多示例请查看 `plugins` 目录下的文件。

## 事件

| 函数 | 描述 |
| --- | --- |
| `OnMessageAsync` 函数 | 当收到新消息时，此函数会被调用 |
| `OnLoaded` 函数 | 当插件全部被加载完后会执行的函数，可以放一些互操作性的初始化代码。 |

### 消息处理链

插件通过异步回调获得处理后的消息链和轻量的消息上下文（平台无关）：

```csharp
public override Task OnMessageAsync(
    bool isMentioned,
    Command? command,
    IReadOnlyList<TypedMessage> messageChain,
    MessageContext context)
{
    // messageChain 中的 Reply、Forward、图片、文件等均为 merrybot:// 本地引用。
    // context 提供会话定位（Session）与发送者/机器人身份（SenderId/SelfId）。
    return Task.CompletedTask;
}
```

使用 `Interop.MessageService` 可按本地引用读取 Reply、Forward 或媒体资源；Core 会复用正在进行的请求并负责持久化。

## API/属性

这些 API/属性在抽象父类中被定义：

| API | Description | Note |
|:---:|:---|:---|
| Actions Actions{get;} | 获取 `Actions`，用于发送消息 | |
| MessageChannel Channel {get;} | 发送消息（来自 Interop），内含日志，失败不抛出 | |
| bool IsEnable {set;protected get;} | 是否启用 | 无论是否启用，插件都会被加载，当为假时 OnMessageReceived 函数不会被调用 |
| string? StartsWith {set;get;} | 该项是属性，若设置，那么只有以 `StartsWith` 开头的消息会触发 `OnMessageReceived` 函数 | |
| ISimpleLogger logger {get;} | 获取 `logger`，用于记录日志 | |
| Interop interop {get;} | 获取互操作性（查找插件、数据持久化、使用Core功能） | |

### 互操作性 - interop

**注意** 对于互操作性，请不要在构造函数中使用（此时插件没有加载完），建议在 `OnLoaded` 函数中使用。

| API/属性 | Description |
|:---:|:---|
| T? FindPlugin\<T\>() | 查找类型为 T 的插件，用于插件互操作性（推荐直接在构造函数中直接注入其他插件实例） |
| IEnumerable\<PluginInfo\> PluginInfoGetter() | 获取所有插件的 PluginInfo |
| PluginStorage PluginStorage {get;} | 获取插件存储 |
| PluginDatabaseScope PluginDatabase {get;} | 获取当前插件的 scoped LiteDB 数据库 |
| ClockService ClockService {get;} | core 拥有的定时任务调度器（生命周期归宿主，插件不负责创建/释放） |
| DelegatingClockExecutor ClockExecutor {get;} | 定时任务执行器注册口：core 先以空转发器建调度器，Agent 插件初始化时设置 `Inner` 注册自己的执行器 |
| T? GetVariable\<T\>(string key) | 获取当前插件命名空间下 `Variable` 中的配置项 |
| List\<MessageInterceptor\> Interceptors | 设置拦截器，拦截特定消息被插件处理 |
| IHostLifecycle Lifecycle | core 生命周期回调：检测更新 / 请求更新（fetch+merge+编译备用槽+切槽重启）/ 重启 / 重载 / 退出 |
| long AuthorizedUser | 获取授权用户的QQ号 |

### 拦截器 - Interceptors

方法签名：

```csharp
public delegate bool MessageInterceptor(MessageContext context)
```

返回 true 拦截，false 不拦截。

### 插件存储 - PluginStorage

对于每个插件，都会分配一个独立的存储服务（依赖 PluginTag 设置的插件 id），以 object 为单位进行储存与读取，现阶段的实现依赖于 NoSQL：

| API | Description |
|:---:|:---|
| Task\<T\> Load\<T\>(T defaultValue) | 异步加载对象，如果不存在则返回默认值 |
| Task Save\<T\>(T data) | 异步存储对象 |

### Scoped 数据库 - PluginDatabase

`PluginStorage` 适合保存一个简单的插件对象或群级对象。需要多个表、索引或复杂查询时，可使用 `Interop.PluginDatabase`；每个插件只会访问以自身 `PluginTag.Id` 为 scope 的 collection。

```csharp
public sealed class Todo
{
    public int Id { get; set; }
    public long GroupId { get; set; }
    public string Content { get; set; } = "";
}

var todos = Interop.PluginDatabase.GetCollection<Todo>("todos");
await todos.EnsureIndexAsync(x => x.GroupId);
await todos.UpsertAsync(new Todo { Id = 1, GroupId = 123, Content = "example" });
```

`GetCollection<T>(name)` 会按需创建当前插件的表；`DropCollectionAsync(name)` 只能删除当前插件 scope 内的表。底层数据库由 Core 管理，插件不需要、也不能自行释放连接。

### 工具类 - `MessageUtils`

| API | Description |
|:---:|:---|
| bool IsEqual(MessageChain? a, MessageChain? b) | 判断两个消息链是否相同 |

### 日志记录器 `logger`

| API | Description |
|:---:|:---|
| void Trace(string message) | 记录踪迹日志 |
| void Debug(string message) | 记录调试日志 |
| void Info(string message) | 记录消息日志 |
| void Warn(string message) | 记录警告日志 |
| void Error(string message) | 记录错误日志 |
| void Fatal(string message) | 记录崩溃日志 |

### PluginTag 类属性标签

构造函数为 `(string id, string name, string description, bool isIgnore=false, PluginType type=PluginType.Interactive)`

参数说明：

- `id` - 插件标识符（英文），用于配置文件命名空间隔离
- `name` - 插件名称（可中文），用于显示
- `description` - 插件描述
- `isIgnore` - 是否忽略加载
- `priority` - 插件优先级，决定加载顺序。值越小，优先级越高
- `type` - 插件类型

当 `isIgnore==true` 时，插件不会被加载。

`PluginType` 可选值：

- `Interactive` - 交互式插件（默认）
- `Background` - 后台插件
- `Admin` - 管理员插件

## Note

如果插件不可用（如不支持当前平台），请在构造函数中抛出 `PluginNotUsableException` 异常。
