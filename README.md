# MerryBot

MerryBot是基于以napcat为上游的机器人框架，使用C#编写，支持插件化开发。

# 配置文件 `setting.toml`

程序依赖 `setting.toml` 进行配置。下面是一个基础的配置示例：

```toml
napcat-server = "ws://localhost:30001"    # napcat websocket地址
napcat-token = "your_token_here"          # napcat token
qq-groups = [114514, 1919810]             # 要监听的qq群号列表
authorized-user = 114514                  # 授权用户（Bot管理员）qq号
machine-code = 0                          # 历史记录机器编号；省略时首次启动自动生成
web-address = "http://localhost:5000"      # WebUI 监听地址；仅绑定本机，远程管理请使用 SSH 端口转发
```

> 💡 Agent 等插件的独立配置由插件配置存储管理，可在 WebUI 中维护；不会写入 `setting.toml`。

# 环境变量支持
`MERRY_BOT`：指向文件夹。如果没有指定，则默认使用工作目录下的`data`文件夹。

程序产生的数据都会保存在这个文件夹下：
1. 日志文件
2. 配置文件
3. 插件存储

# 整体架构
![Architecture](arch.svg)

# 主要内置插件

## AI机器人
在历史后台的“LLM 配置”页搜索并选择 models.dev Provider，再添加所需模型；目录会本地缓存，点击刷新才联网更新。填写 OpenAI Chat Completions 兼容的 API 地址和 Key 后，Agent 会使用设为默认的模型。Provider、模型和 Key 都保存在插件数据库；Key 不写入 `setting.toml`，页面也不会读回原文。

内置了如下function call:
- bing搜索
- 网页浏览
- 查看时间
- 发送语音
- 查看微博热搜
- Linux Shell 终端（支持同步/异步两种模式）
- Markdown 功能，使用Chrome将Md渲染为图片后发送（latex,mermaid supported）

### Shell 工具

提供三种 shell 调用方式：

| 工具 | 模式 | 适用场景 | 默认超时 |
|------|------|----------|----------|
| `shell_sync` | 同步等待 | `ls`、`cat` 等快速命令 | 10s |
| `shell` | 异步返回 task_id | 编译、安装等耗时任务 | 30s |
| `shell_result` | 查询异步任务 | 拿 `shell` 的结果 | - |

每个异步命令会创建独立的 Terminal 实例，支持真正并行执行。超时时间可通过 `timeout` 参数自定义。

### Skills 技能系统

在数据目录下创建 `skills/` 文件夹（如 `data/skills/`），放入技能文件，AI 会自动识别并在需要时读取执行。也可在 WebUI 的“Skill 管理”页上传单文件 `.md` 或目录型 `.zip`，并通过 `.disable` 标记禁用：

```bash
# 示例：创建一个翻译技能
echo "你是一个翻译助手，请将用户输入翻译为英文。" > data/skills/翻译.md
```

AI 收到匹配的请求时，会先调用 `skill_read` 读取技能内容，再按指令执行。

### Memory 记忆系统

每个群有独立的持久化记忆空间，按 `sessionKey` 隔离并存储在 Agent 插件数据库中。WebUI 的“记忆管理”页会用 Core 历史数据库将 QQ 群 ID 显示为群名。

| 工具 | 功能 |
|------|------|
| `save_memory` | 保存或更新一条记忆 |
| `recall_memory` | 查看当前群所有记忆的 key 列表 |
| `query_memory` | 通过 key 读取具体内容 |
| `delete_memory` | 删除指定记忆 |

每次对话开始时，AI 会在 system prompt 中看到所有记忆的 key 列表，按需调用 `query_memory` 查看具体内容。

使用示例：
```
用户: 我喜欢用中文回复
AI → save_memory("用户偏好", "喜欢用中文回复")
→ 写入当前 sessionKey 对应的数据库记录

[下一次对话]
system prompt: ## 已记忆的信息
               - 用户偏好
AI → query_memory("用户偏好") → "喜欢用中文回复"
```

*: 网络访问相关funtion call 通过seleium操纵chrome(chromium)（需要提前安装）实现。如果chrome不存在于系统环境中，则需要在环境变量`CHROME_BIN`中指定chrome的路径。

如果是命令行环境，需要为chrome安装字体支持
```bash
sudo apt-get update
# 安装彩色 Emoji 字体
sudo apt-get install -y fonts-noto-color-emoji
# 安装 Google 诺托字体
sudo apt-get install -y fonts-noto-cjk
# 其他生僻字 optional
sudo apt install fonts-noto-cjk-extra fonts-hanazono
# 刷新字体缓存
sudo fc-cache -fv
```


## 群刊
当群聊消息达到一定数量（默认 500 条）时，自动调用 AI 生成一份幽默、戏剧性强的群聊周刊/日报。

AI 会分析聊天记录中的梗和热点，自动提取目录并生成包含前言、正文章节和结语的完整内容，最后通过浏览器渲染为精美的 Markdown 图片发送。

支持以下命令：
- `/highlights status` - 查看当前群聊的消息计数进度
- `/highlights flush` - 立即强制生成一份当前的消息群刊
- `/highlights reset` - 重置当前的消息计数
- `/highlights` - 显示插件帮助信息及当前进度

可在配置文件中通过以下变量进行自定义：
- `message-count`: 触发生成的自动阈值（默认 500）
- `section-count`: 生成的章节数量（默认 3）
- `highlights-prompt`: AI 生成风格的系统提示词


## 自动加一
当有刷屏消息时，自动发送刷屏信息。

## 终端
提供linux终端，需要配置merrybot用户且当前用户有权限切换到merrybot用户。
## 快速更新
自动执行`git fetch && git merge`，并以101状态码退出程序。
配合`launch.sh`脚本可实现自动编译运行。
## MainPlugin
特权插件，用于管理bot。在未监听的群聊中使用`@bot /activate`即可激活bot，使用`@bot /deactivate`即可取消bot监听。

# 插件开发
1. 一个插件应当放在`plugins`项目的一个文件中
2. 应当继承于`Plugin`抽象类
3. 有且只有一个构造函数，存在类型为 `PluginInterop`的参数；插件之间不再依赖消息记录器或存储管理器
4. 在类前面使用属性`PluginTag(string id, string name, string description, [bool isIgnore=false], [PluginType type=PluginType.Interactive])`

主程序会通过反射加载`plugins`项目下的所有插件类，因此需要满足上述条件。

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

更多示例请查看`plugins`目录下的文件。

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
这些 API/属性 在抽象父类中被定义

|API|Description|Note
|:---:|:---|:---
|Actions Actions{get;}|获取`Actions`，用于发送消息
|MessageChannel Channel {get;}|发送消息（来自 Interop），内含日志，失败不抛出|
|bool IsEnable {set;protected get;}|是否启用|无论是否启用，插件都会被加载，当为假时OnMessageReceived函数不会被调用
|string? StartsWith {set;get;}|该项是属性，若设置，那么只有以`StartsWith`开头的消息会触发`OnMessageReceived`函数
|ISimpleLogger logger {get;}|获取`logger`，用于记录日志
|Interop interop {get;}|获取互操作性（查找插件、数据持久化、使用Core功能）|

### 互操作性-interop
**注意** 对于互操作性，请不要在构造函数中使用（此时插件没有加载完），建议在`OnLoaded`函数中使用

|API/属性|Description|
|:---:|:---|
|T? FindPlugin\<T\>()|查找类型为T的插件，用于插件互操作性(其实笔者更推荐直接在构造函数中直接注入其他插件实例)|
|IEnumerable<PluginInfo> PluginInfoGetter()|获取所有插件的PluginInfo|
|PluginStorage PluginStorage {get;}|获取插件存储|
|PluginDatabaseScope PluginDatabase {get;}|获取当前插件的 scoped LiteDB 数据库|
|T? GetVariable<T>(string key)|获取当前插件命名空间下`Variable`中的配置项|
|List<MessageInterceptor> Interceptors|设置拦截器，拦截特定消息被插件处理|
|Action<int> Shutdown|关闭程序，参数为退出码|
|long AuthorizedUser|获取授权用户的QQ号|

### 拦截器-Interceptors
方法签名：
```csharp
public delegate bool MessageInterceptor(MessageContext context)
```
返回true拦截，false不拦截。

### 插件存储-PluginStorage

对于每个插件，都会分配一个独立的存储服务（依赖PluginTag设置的插件id），以object为单位进行储存于读取，现阶段的实现依赖于NoSQL

|API|Description|
|:---:|:---|
|Task\<T\> Load\<T\>(T defaultValue)|异步加载对象，如果不存在则返回默认值|
|Task Save\<T\>(T data)|异步存储对象|

### Scoped 数据库-PluginDatabase

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

### 工具类-`MessageUtils`
|API|Description|
|:---:|:---|
|bool IsEqual(MessageChain? a,MessageChain? b)|判断两个消息链是否相同

### 日志记录器`logger`

|API|Description|
|:---:|:---|
|void Trace(string message)|记录踪迹日志
|void Debug(string message)|记录踪迹日志
|void Info(string message)|记录消息日志
|void Warn(string message)|记录警告日志
|void Error(string message)|记录错误日志
|void Fatal(string message)|记录崩溃日志


### PluginTag类属性标签

构造函数为`(string id, string name, string description, bool isIgnore=false, PluginType type=PluginType.Interactive)`

参数说明：
- `id` - 插件标识符（英文），用于配置文件命名空间隔离
- `name` - 插件名称（可中文），用于显示
- `description` - 插件描述
- `isIgnore` - 是否忽略加载
- `priority` - 插件优先级，决定加载顺序。值越小，优先级越高
- `type` - 插件类型

当`isIgnore==true`时，插件不会被加载

`PluginType` 可选值：
- `Interactive` - 交互式插件（默认）
- `Background` - 后台插件
- `Admin` - 管理员插件

###  Note

如果插件不可用（如不支持当前平台），请在构造函数中抛出 `PluginNotUsableException` 异常
