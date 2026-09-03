# AGENTS.md

本文档供 AI 编码代理阅读，描述了 MerryBot 仓库的架构、构建方式与开发约定。所有信息基于当前代码核实，如有疑问以代码为准。

## 项目概述

MerryBot 是一个基于 **NapCat** 上游的 QQ 机器人框架，使用 **C#（.NET 10）** 编写，支持插件化开发。主程序通过 WebSocket 连接 NapCat（OneBot 协议实现），监听 QQ 群消息并分发到内置插件；内置了基于 LLM 的 AI 机器人插件（Agent），支持工具调用（function calling）、定时任务、技能（Skills）与记忆系统；同时内嵌一个 Blazor WebUI 历史后台用于查看消息记录、管理群组、维护 LLM Provider/模型/Key、编辑配置。

- 仓库：https://github.com/57UU/MerryBot
- 架构图见根目录 `arch.svg`
- 代码与注释的主要语言为中文（`AGENTS.md` 亦使用中文）

## 技术栈

| 领域 | 选型 |
| --- | --- |
| 运行时 | .NET 10（全部项目 `net10.0`，唯一例外是 `ModelsDev.Sdk` 为 `net8.0` 的独立发布 SDK） |
| 消息通信 | `Websocket.Client` 5.5.0（NapCat WebSocket 协议） |
| 存储 | LiteDB 5.0.21 + LiteDB.Async 0.1.8（本地 NoSQL，`plugin_data.db` 与 `group_history.db`） |
| 日志 | NLog 6.2.0（宿主）；插件与库层使用 `CommonLib` 的 `ISimpleLogger` |
| WebUI | ASP.NET Core Blazor（InteractiveServer 渲染模式）+ Minimal API |
| 浏览器 | Selenium.WebDriver 4.47.0（无头 Chrome/Edge，`Browser` 项目：网页搜索/抓取、Markdown 渲染为图片） |
| Markdown | Markdig 1.3.2（`Markdown2Html` 项目） |
| 定时任务 | Cronos 0.13.0（Linux 五字段 cron，含 `@daily` 等别名） |
| LLM | 自研抽象 `LlmBackend`/`LlmClient`，支持 OpenAI Chat Completions / Responses / Anthropic Messages 三种格式 |
| 其他 | IdGen 3.0.7（雪花 ID）、Microsoft.AspNetCore.DataProtection（API Key 加密）、YamlDotNet（Agent.Tui 配置）、ConsoleTables；终端 UI 使用自研框架 `MerryBot.Tui`（原 Terminal.Gui 已移除） |
| 测试 | xunit.v3 4.0.0（`xunit.v3` 包，禁用 MTP、走 VSTest 适配器）+ Microsoft.NET.Test.Sdk + coverlet + Microsoft.Extensions.TimeProvider.Testing（`FakeTimeProvider`） |

## 项目结构（解决方案 `MerryBot.sln`）

解决方案分为两个逻辑文件夹：**Bot**（主程序相关）与 **Agent**（LLM Agent 相关）。

### Bot 组

- **`MerryBot/`** — 主程序（Exe）。入口 `Entry.cs` 为顶层语句。核心宿主为 `Logic`（`internal partial class`，拆分为多个文件）：
  - `Logic.cs`：组装所有组件（HistoryRecorder、MessageService、WebUI、ConfigRegistry、HostLifecycle、ClockService）、重连循环、群消息入口
  - `Logic.Plugins.cs`：插件发现与加载（反射）、WebUI API 注册、`Shutdown`
  - `Logic.Message.cs`：消息分发（按插件逐个调用 `OnMessageAsync`，含拦截器）
  - `Logic.Config.cs`：按插件 Id 加载/保存 `IPluginConfig`
  - `Logic.Event.cs` / `Logic.Groups.cs`：通知事件处理与群组管理
  - 其他：`ConfigManager`（核心配置）、`HostLifecycle`（版本/更新/重启/重载/退出）、`PluginInitializer`（插件依赖注入与拓扑排序）、`MessageService`（消息持久化与资源引用）、`BotMessageChannel`、`ClockStore.cs`（内含 `internal CoreClockStore`，定时任务持久化）、`Utils`、`NLogAdapter`、`PluginLoggerAdapter`
- **`NapcatClient/`** — NapCat 客户端库。`BotClient`（连接与事件分发）、`WebSocketAdapter`、`Actions`（发消息等 API）、`Event.cs`、`EventType.cs`（各通知事件类型，单文件）、`MessageType.cs`（`TypedMessage` 层次：TextData/AtData/ImageData/ReplyData/ForwardData 等，含 `MessageTypeString.cs`/`TypedJsonConverter.cs` 序列化）、`Msg.cs`、`BotUtils`、`QqFace`、`AdapterState`
- **`DataProvider/`** — 插件存储数据库 `PluginStorageDatabase`（LiteDB 封装，`plugin_data.db`；`PluginStorageDatabase.Migrations.cs` 提供 schema 迁移，当前版本 1，启动时 `MigrateAsync()` 执行）与 `PluginDatabaseScope`（按插件 Id 隔离的集合视图）
- **`DataService/`** — 历史记录 `HistoryRecorder`（`group_history.db` + `storage/` 对象存储：群消息/图片床/文件床/群事件等）+ `AiMessageStore`（ai_messages 集合与 token 用量聚合；与 HistoryRecorder 共享同一数据库，由其构造组合、统一负责迁移与生命周期）、`ObjectStorage`/`FileSystemObjectStorage`、`HistoryModel`、`TokenUsageAggregator`（ai_messages token 用量分桶/按会话聚合的 internal 纯函数）、`IdGenConfig`
- **`plugins/`** — 内置插件（`RootNamespace` 为 `BotPlugin`）。包含插件基础设施（`_pluginBase.cs` 的 `Plugin` 抽象类、`_interface.cs` 的 `PluginInterop`/`PluginTag`/`PluginStorage`、`_common.cs` 的 `MessageContext`/`SessionKey`、`_interface.event.cs` 的事件注册）与具体插件（见下文"内置插件"）
- **`MerryBot.WebUI/`** — Blazor 历史后台。`Program.CreateApp(historyRecorder, webAddress)` 由宿主在进程内调用（也支持独立 `Main` 运行）；`Api/` 下为各功能区的 Minimal API mapper（`ConfigApiMapper`、`AdvancedConfigApiMapper`、`StatusApiMapper`、`GroupApiMapper`、`LogApiMapper`、`UpdateApiMapper`、`LlmProviderApiMapper`、`SkillApiMapper`、`MemoryApiMapper`、`ContextSnapshotApiMapper`、`ConfigRegistry`、`ModelsDevCatalogService`）；`Components/Pages/` 为页面（群消息、AI 消息、会话 AI 消息、LLM 配置、记忆、技能、统计、Token 用量、配置编辑、高级配置、日志、群管理、转发消息等）

### Agent 组

- **`Agent/`** — 通用 LLM Agent 核心（不依赖 NapCat/QQ）：`Agent.cs`（对话循环、上下文压缩）、`Agent.RunIteration.cs`（单轮迭代与工具执行）、`Agent.Options.cs`、`Agent.ToolSet.cs`、`Context.cs`/`ContextManager.cs`/`ContextHistory.cs`、`VisionRouter.cs`（主模型无视觉能力时用辅助模型描述图片）、`AgentLogEvent.cs`
- **`Agent.Session/`** — 会话层：`AgentSession`（串行消息队列）、`AgentSessionManager`（空闲淘汰）、`AgentSessionClockExecutor`（把定时任务投给会话）、`ClockService`（core 拥有的 cron 调度器，持久化、misfire 跳过、超时、按 `(pluginId, sessionId)` 双重隔离共享给所有插件）、`ClockScope`（绑定插件 Id 的门面，供 `PluginInterop.Clock` 注入）、`ClockModels`/`ClockAbstractions`（`DelegatingClockExecutor` 按 pluginId 注册/路由）/`InMemoryClockStore`、`Cron.cs`（定时任务 LLM 工具集）、`Terminal.cs`（常驻 bash 进程封装）/`TerminalToolSet.cs`（shell 工具）
- **`Agent.Tools/`** — LLM 工具集：`WebTools`（web_search/web_fetch，Bing）、`SkillToolSet`/`FileSkillManagementService`、`SubAgentToolSet`、`TimeToolSet`、`TodoListToolSet`
- **`Agent.Tui/`** — 独立的终端聊天客户端，复用 Agent/Agent.Session/Agent.Tools，直接连 OpenAI 兼容 API；终端 UI 基于自研 `MerryBot.Tui/` 框架
- **`MerryBot.Tui/`** — 自研终端 UI 框架（`Ansi`/`Component`/`TerminalDriver`/`RawMode`/`KeyParser`/`SelectList`/`TextWidth`/`TuiApp`/`TuiScreen`/`ConsoleUtf8` 等），替代原 Terminal.Gui，被 `Agent.Tui` 引用。`ConsoleUtf8` 在 `TuiApp.Run` 启动时显式把 Windows 控制台代码页切到 65001(UTF-8) 并对 stdout 启用 VT 处理（退出恢复），解决传统 conhost（GBK 代码页）下中文/ANSI 乱码
- **`Browser/`** — Selenium 无头浏览器封装（`BrowserService` 命名空间）：Chrome/Edge 自动探测与反检测（`StealthService`）、`Browser.Actions.cs`/`Browser.Helpers.cs`/`BrowserUtility.cs`；供 `MessageTool`/`WebTools` 做网页搜索/抓取与 Markdown 渲染
- **`Markdown2Html/`** — Markdig 封装的 `MarkdownConverter`（Markdown → HTML）
- **`LlmClient/`** — LLM 客户端：`Client`（重试：限速避让/指数退避；流式基于 reset 语义——任何可重试失败含中途断流，预算内回调 `IResettableStreamSink.OnReset` 后重建流，消费者丢弃该段增量；正文检出工具调用标记走同一 reset 重试；后端可运行时替换 `UpdateBackend`）、`ClientConfig`、`IResettableStreamSink`/`StreamResetReason`、`StrayToolCallDetector`（正文开头/结尾窗口的结构化检测：DSML 特殊 token / XML 工具标签 / JSON 工具调用结构，仅携带工具的请求启用）
- **`LlmBackend/`** — LLM 后端抽象：`Backend` 接口（流式为推送式 `IStreamSink` 回调：OnTextDelta/OnReasoningDelta/OnCompleted，中途异常归一化为 LlmException）、`ChatCompletionBackend`（OpenAI 兼容 `/chat/completions`）、`AnthropicBackend`、`ResponsesBackend`（三者构造器均有可选 `sessionKey`：OpenCode 会话亲和头 `x-opencode-session`，仅 baseUrl 落在 opencode.ai 时发送；传入则原样使用，未传则实例级稳定随机数；`OpenCodeAffinity` 集中决议/加头）、`LlmOptions`（含 `WithoutTools()`）、`Message`/`ToolCall`/`TokenUsage`（归一化约定：所有后端 `promptUsage` = 含缓存命中的完整输入，`cachedUsage ⊆ promptUsage`；Anthropic 的 cache_read/cache_creation 在构造时并入 prompt）、`Tools.cs`（`ToolDef`/`FunctionDef`）、`MimeTypes`、`LlmDefaults`（超时默认值）、`Errors`/`BackendErrors`
- **`ModelsDev.Sdk/`** — 独立的 models.dev 模型目录 SDK（`net8.0`），含 `ModelsDevClient`、`ModelQueryBuilder` 与模型/Provider 元数据类型
- **`CommonLib/`** — 公共契约库：`ISimpleLogger`/`ConsoleLogger`/`LogLevel`、`ExitCode`（101/102/103）、`HostLifecycleContracts`（`IHostLifecycle`/`UpdateCheckResult`）、`ContextSnapshotContracts`/`MemoryManagementContracts`/`SkillManagementContracts`、`ConfigDescriptionAttribute`、`RequestCaching`、`Format`

### 测试项目

- **`MerryBot.Test/`** — xunit 单元测试：`ClockServiceTests`（调度器，用 `FakeTimeProvider`）、`ClockServiceStoreIntegrationTests`、`CoreClockStoreTests`、`AgentCompactionTests`、`AgentConcurrencyLimitTests`（并发工具调用/子任务/后台任务上限）、`ConfigRegistryTests`、`TokenUsageAggregatorTests`（token 用量分桶/会话聚合）、`LlmBackendStreamTests`（流式块解析，`InternalsVisibleTo` 访问 internal 成员）、`StrayToolCallRetryTests`（流式 reset 重试与正文工具调用标记检测）、`RequestCachingTests`、`VisionRouterTests`、`ChromeDetectionTests`（浏览器可用性探测）、`ToolSetFailureTests`（工具失败语义）、`TerminalBackgroundTimeoutTests`（shell 前台超时转后台）；辅助类 `FakeClockStore`/`RecordingExecutor`/`TestClock`
- **`ModelsDev.Sdk.Test/`** — xunit 测试（SDK 序列化/查询）
- **`Browser.Test/`** — 浏览器手工测试台（Exe，非自动化测试）
- **`Test/`** — 手工测试台（Exe，非自动化测试，通常不需要维护）

## 运行时架构

### 启动流程（`MerryBot/Entry.cs`）

1. 数据目录 = 环境变量 `MERRY_BOT` 或默认 `data`；日志文件在 `<data>/log/<时间戳>.log`，插件数据库 `<data>/plugin_data.db`
2. 打开 `PluginStorageDatabase` 并执行 schema 迁移（`MigrateAsync`，幂等），随后 `ConfigManager.Initialize` 从插件数据库加载核心配置
3. 初始化 NLog（彩色控制台 + 文件目标）
4. 构造 `BotClient`（NapCat WebSocket 客户端）与 `Logic`。构造不再同步等待登录信息，NapCat 未启动也能启动进程
5. 等待 Ctrl+C → `Logic.Shutdown()`（幂等，只执行一次）

### 组件装配（`Logic`）

`Logic` 构造时按顺序创建：

- `HistoryRecorder`（`group_history.db` + `storage/`，机器码来自核心配置 `MachineCode`，`<0` 时自动生成 0–31 并落库）
- `MessageService`（消息入库、`merrybot://` 资源引用、AI 消息审计）
- WebUI（`MerryBot.WebUI.Program.CreateApp`）与 `ConfigRegistry`，随后注册各 API mapper
- `HostLifecycle`（git 检测更新 / 编译槽 / 重启 / 重载 / 退出）
- `ClockService`（core 拥有，存储为 `PluginStorageDatabase.CreateScope("clock", prefix: "core")`，schema v2 任务带 `PluginId`、`Content` 为 `object?` 弱类型存储；调度器先于插件创建，插件经 `PluginInterop.Clock`（`ClockScope`）按 pluginId 隔离访问）
- `LoadPlugins()`（反射加载插件）
- 重连循环（适配器由宿主按 `ReconnectIntervalSeconds` 轮询连接，适配器自身不重连）与事件处理器注册

### 消息处理链

```
NapCat WebSocket → BotClient.WebSocket_OnMessage
  → OnGroupMessageReceived (group_id, messageChain, ReceivedGroupMessage)
  → Logic.OnGroupMessageReceived
      ├─ 过滤未监听群（Logic.QqGroupIDs.Contains(groupId)）
      ├─ messageService.Ingest（入库、合并相邻文本、生成资源引用）
      ├─ ExtractMessage（提取 @机器人 标记 isMentioned 与文本）
      ├─ ParseCommand（以 / 开头的命令，Args 为其余参数）
      └─ OnMessage → 遍历每个启用的插件：
          ├─ 先执行该插件的 Interceptors（返回 true 则跳过该插件）
          ├─ 克隆消息链后调用 plugin.OnMessageAsync(...)
          └─ 插件内异常仅记日志，不中断分发
```

### 插件系统

插件是 `plugins` 项目中的类（主程序通过反射扫描 `typeof(Plugin)` 所在程序集），必须满足：

1. 继承 `Plugin` 抽象类（`plugins/_pluginBase.cs`）
2. 类上标注 `[PluginTag(id, name, description, isIgnore=false, type=PluginType.Interactive)]`；`id` 用于配置与存储命名空间隔离
3. 有且只有一个构造函数，参数类型来自：`PluginInterop`（注入）、其他插件实例（依赖注入，拓扑排序保证先构造依赖方）、`IPluginConfig` 子类（按插件 Id 从数据库加载，缺失时生成默认值落盘）
4. 平台不支持时在构造函数抛出 `PluginNotUsableException`（被捕获并按"跳过该插件"处理，不影响其他插件）

生命周期：构造 → （全部加载完）`OnLoaded()` → 每条消息 `OnMessageAsync(...)` → 关闭时按依赖逆序 `Dispose()`。**不要在构造函数中使用 `Interop` 的互操作能力**（此时插件未加载完），请在 `OnLoaded` 中使用。`IsEnable=false` 时 `OnMessageAsync` 不会被调用。

`PluginInterop`（`plugins/_interface.cs`，record）提供：日志（`ISimpleLogger`）、群列表（`GroupId`）、`PluginInfoGetter`、`PluginStorage`（对象级读写，内含 `PluginDatabaseScope` 即 scoped LiteDB 集合）、`Clock`（`ClockScope` 门面，绑定本插件 Id 的定时任务调度器访问视图——CRUD 与日志查询按 `(pluginId, sessionId)` 隔离；执行器经 `RegisterExecutor` 注册，调度器按 `task.PluginId` 路由到各插件自己的执行器；调度器生命周期归宿主。`ClockTask.Content` 为 `object?`：可为 null 或插件自定义模型，agent 执行器要求非空字符串）、`Lifecycle`（`IHostLifecycle`）、`AuthorizedUser`、`PathPrefix`、`EventRegister`（`_interface.event.cs` 的通知事件注册）、`MessageService`、`Channel`（发消息）、`Interceptors`（拦截器，仅拦截当前插件的消息）。另有 `internal FindPlugin<T>()` 按类型查找插件（仅同程序集可见）。

### 进程生命周期与更新（`IHostLifecycle` / `launch.sh`）

- 退出码契约（`CommonLib/ExitCode.cs`）：`101` RESTART（重新编译当前槽并重启）、`102` RELOAD（不编译直接重启）、`103` PREBUILT（已切槽，直接换槽重启）
- `launch.sh`：双槽蓝绿部署循环。`build/active_slot` 记录当前槽（A/B），`build.sh <target_dir>` 将程序发布到槽目录；按退出码决定重建/重载/切槽
- `build.sh`：服务器发布脚本。先 `dotnet build-server shutdown`（避免 Roslyn 文件锁），按架构选择 `linux-x64`/`linux-arm64`，先发布 `MerryBot.WebUI` 拷贝 `wwwroot`，再发布 `MerryBot`
- `HostLifecycle`：`/update` 流程 = `git fetch + merge` → 编译备用槽 → 更新 `active_slot` → 以 `103` 退出；重启后 `ViewVersion` 插件在 `OnLoaded` 消费 core 写入的待通知目标并补发结果到群

### Agent 会话模型

- 每个 QQ 群一个会话键 `SessionKey("qq", "group", 群号)`；`AgentSessionManager` 管理会话，空闲默认 12 小时淘汰（`IdleSessionTimeoutHours` 可配）
- 群消息按群排队串行处理（`PendingGroupMessages` 调度循环），限速默认 5 次/20 秒（`RateLimiter`）
- 控制命令：`@bot /new`（清空上下文）、`@bot /compact [主题]`（按主题压缩）、`@bot /stop`（带外立即执行：取消正在生成的回复并丢弃该群排队消息，`AgentSession.Stop()` 取消当前对话的链接 CTS）、消息含 `#新对话` 关键字亦触发新会话
- 上下文超阈值（`ContextCompactRatio`）时用 LLM 摘要压缩，历史持久化到 agent 作用域 LiteDB（`DatabaseContextHistory`）
- 工具调用：`MessageTool`（发消息/看图片）、`TodoListToolSet`、`WebTools`（经 `Browser`）、`PromptToolSet`（动态提示词）、`SkillToolSet`、`Cron`（定时任务）、`MemoryToolSet`（记忆）、`SubAgentToolSet`（子任务代理，不嵌套自身）、`TerminalToolSet`（仅 `AllowShell` 开启时注册，命令以 `shell-user` 身份 `sudo -u` 执行；shell 前台支持 `background_on_timeout`，超时不终止而是自动转后台任务并以 `TERMINAL_TASK_RESULT` 通知）
- 视觉：主模型具备 `ImageInput` 能力时图片直接进对话；否则按 `vision-llm` 配置的辅助模型列表逐级降级（`VisionRouter`）

### WebUI

- Blazor InteractiveServer，与主程序同进程运行，监听启动配置 `setting.toml` 的 `web-address`（默认 `http://localhost:5000`）
- 提供 `/api/...` Minimal API：状态、群组管理、日志、配置编辑、高级配置、LLM Provider/模型/Key 管理、Skill 上传/禁用、记忆管理、上下文快照、更新检测、定时任务（`ClockApiMapper`，跨插件列出/编辑/删除与日志查询）
- 图片/文件经 `/api/image/{id}`、`/api/file/{id}`、`/api/resource` 由本地存储提供，消息链中的媒体均为 `merrybot://` 本地引用，前端不直连远端 URL
- **设计决策（by design）**：WebUI **不做内置鉴权**，默认仅绑定 `localhost` —— 这是有意为之，目的是保持配置/管理入口的简洁性，避免引入账号体系与登录复杂度。**远程访问的推荐方式是 SSH 端口转发**（如 `ssh -L 5000:localhost:5000 user@host`），由 SSH 承担认证与加密，WebUI 自身不需要也不应暴露到公网。若用户自行将 `setting.toml` 的 `web-address` 改为 `0.0.0.0`，则须自行经受控内网或 HTTPS 反向代理保护，风险自担。监听地址不在 WebUI 中提供修改入口（引导问题：WebUI 挂了就改不回来），只能改 `setting.toml` 后重启

## 数据与存储

| 路径（相对数据目录） | 内容 |
| --- | --- |
| `plugin_data.db` | 核心配置（`core` 命名空间）、插件配置（`Plugin_Config_Table`）、插件对象数据（`Plugin_Data_Table`）、各插件 scoped 集合（LLM Provider/模型/Key、Agent 会话历史/记忆/定时任务等） |
| `group_history.db` + `storage/` | 群消息、图片/文件（按 SHA-256 hash 去重）、群事件、转发消息、群名、AI 消息审计（`ai_messages`：每轮 user/assistant/tool 消息均落库，assistant 的工具调用请求以函数调用形式 `name(参数JSON)` 随正文落库；assistant 行带 token 用量：InputTokens/OutputTokens/CachedTokens，iteration 中间轮用量也计入，供 WebUI Token 用量页聚合）、资源引用表 |
| `log/` | NLog 日志，按天命名 `bot-<日期>.log`（按天/10MB 归档、保留 30 份），layout `${longdate}|${level:uppercase=true}|${logger}|${message}`；WebUI `/logs` 页按 `*.log` 枚举浏览历史 |
| `skills/` | Agent 技能文件（`.md`，可用 `.disable` 标记禁用） |
| `models.dev-api.json` | models.dev 目录缓存（平时搜索优先本地缓存） |
| `llm-provider-key-ring/` | ASP.NET DataProtection 密钥环（用于加密 LLM API Key） |

数据库 schema 迁移：`PluginStorageDatabase.MigrateAsync`（幂等，当前版本 1）在 `Entry.cs` 打开数据库后、初始化配置前执行；`HistoryRecorder.MigrateAsync`（幂等，当前版本 3）在启动时执行。

## 日志体系（统一出口 = NLog）

- **统一抽象**：`CommonLib.ISimpleLogger`（`LogLevel`/`ConsoleLogger` 同文件）。接口用 DIM 提供 `Log(level,msg)`、`X(Exception,msg)`、`X(format,args)` 重载；DIM 方法仅在 `ISimpleLogger` 类型变量上可见。
- **全局门面**：`CommonLib.SimpleLog.Default`，宿主 `Entry.cs` 在 NLog 配置后替换为 `new NLogAdapter("CommonLib")`。库代码规范：实例类用可选构造参数 `ISimpleLogger? logger = null`（体内 `_logger ??= SimpleLog.Default`），静态方法/无注入点用 `SimpleLog.Default`。**禁止再直连 `ConsoleLogger.Instance` 或裸 `Console.WriteLine/Error` 记业务日志**（Tui 终端诊断除外）。
- **NLog 桥**（宿主 `MerryBot` 内）：`NLogAdapter`（logger 名参数化，默认 `NapcatClient`，给 BotClient/WebSocketAdapter/Actions/BotMessageChannel）；`PluginLoggerAdapter` 的 `PluginLogger(tag)`（logger 名 `plugin:<tag>`，给插件）。NLog 级别规则 Debug~Fatal（Trace 丢弃，供高频诊断如模型增量）。
- **Agent 引擎事件**：Agent 组（Agent/LlmClient/LlmBackend）不依赖 CommonLib；`AgentOptions.OnLog` 回调由 `plugins/Agent.LogBridge.cs` 桥接到插件 Logger（`Agent.Create.cs` 已接线），会话/工具调用/压缩/流式重置等事件按级别映射，高频增量落 Trace。
- **WebUI 日志页**：`LogApiMapper` 的 `/api/logs/current` 支持 `lines/level/keyword/file` 后端过滤（向后多扫），`/api/logs/files` 列历史文件；`Logs.razor`（`/logs`）3 秒轮询、文件下拉切换、搜索防抖。WebUI 内部 ASP.NET ILogger（M.E.L.）通道保留，但与 NLog 文件不互通。

## 配置

**配置主体存放在 `plugin_data.db` 中；仅启动必需项（WebUI 监听地址 `web-address`）在 `<data>/setting.toml`，见下文"启动配置"。**（`README.md` 中关于 `setting.toml` 的描述为历史文档，已过时；`Configuration.md` 已同步当前架构。WebUI 的"配置编辑"页通过 `ConfigRegistry` 读写数据库中的配置对象。）

- 核心配置：`MerryBot/ConfigManager.cs` 中的 `Config` 类（`NapcatServer`、`NapcatToken`、`QqGroups`、`AuthorizedUser`、`MachineCode`、`ResourceSizeLimitMb`、`ReconnectIntervalSeconds`），以 `core` 命名空间存入数据库
- 启动配置：`MerryBot/StartupConfig.cs` 加载 `<data>/setting.toml`（当前仅 `web-address`，WebUI 监听地址，默认 `http://localhost:5000`；YAML 语法，YamlDotNet 解析——与 `Agent.Tui` 的 `TuiConfigStore` 同库同版本；文件缺失时生成带注释模板，非法值/解析失败回退默认）。启动必需项不放进 WebUI 可编辑配置，避免引导问题
- 插件配置：实现 `IPluginConfig` 的类，按插件 Id 存取；通过 `[ConfigDescription]` 标注中文说明，供 WebUI 渲染
- LLM Provider/模型/Key：由 `llm-provider` 插件管理，存于其 scoped 集合；Key 用 DataProtection 加密，WebUI 不回显明文
- Agent 配置（`AgentConfig`，`plugins/Agent.Config.cs`）：`LlmModel`、`AiPrompt`、`MaxIterations`、`ContextCompactRatio`、`VisionLlmModels`（列表）、`VisionPrompt`、`AllowShell`、`ShellUser`、`IdleSessionTimeoutHours`、`MaxImageSizeMb`、`MaxConcurrentToolCalls`、`MaxSubagents`、`MaxBackgroundTasks` 等

## 构建与测试

需要 **.NET 10 SDK**（当前 10.0.400）。

```bash
# 构建整个解决方案（已验证通过：0 警告 0 错误）
dotnet build MerryBot.sln -c Debug

# 运行单元测试（已验证：MerryBot.Test 74 通过；ModelsDev.Sdk.Test 61 通过）
dotnet test MerryBot.Test/MerryBot.Test.csproj -c Debug
dotnet test ModelsDev.Sdk.Test/ModelsDev.Sdk.Test.csproj -c Debug
```

服务器发布（Linux）：

```bash
./build.sh <target_dir>   # 按架构发布到目标目录
./launch.sh [-f]          # 双槽运行；-f 强制重建当前槽
```

`.vscode/tasks.json` 提供 build/publish/watch 任务。注意 `.vscode/launch.json` 的 program 路径仍写 `net8.0`，已过时（当前目标框架为 `net10.0`）。

## 代码风格与约定

- 遵循根目录 `.editorconfig`：4 空格缩进、LF 行尾（`insert_final_newline = false`）、块作用域命名空间、`csharp_style_var_* = false`（**避免 `var`，用显式类型**）、类型/非字段成员 PascalCase、接口 `I` 前缀、表达式体属性/getter
- 所有项目启用 `Nullable` 与 `ImplicitUsings`
- 注释以中文为主，重要逻辑用 XML doc 注释说明"为什么"（常见模式：注释解释取舍/边界/兼容性）
- 宿主日志用 NLog；库与插件用 `CommonLib.ISimpleLogger`（`Logger`）
- 插件间依赖通过构造函数注入（`PluginInitializer` 拓扑排序），不要手工 new 其他插件
- 新增插件必须：放在 `plugins` 项目、继承 `Plugin`、标注 `[PluginTag]`、唯一构造函数含 `PluginInterop`
- 新增 WebUI 页面/API：在 `MerryBot.WebUI/Api/` 加 mapper，由 `Logic`（`Logic.Plugins.cs` 的 `RegisterWebUi`）注册；管理类接口（如 `ILlmProviderManagementService`）不应依赖 ASP.NET 类型

## 测试约定

- 单元测试用 xunit；时间敏感逻辑使用 `Microsoft.Extensions.Time.Testing.FakeTimeProvider`（真实时间断言不稳定，如 `ClockServiceTests` 只用 `FakeClockStore`/`RecordingExecutor`/`TestClock`，绝不依赖真实时钟）
- 需要访问被测项目 `internal` 成员时，在被测项目加 `InternalsVisibleTo`（`MerryBot`/`LlmBackend`/`LlmClient`/`Browser` → `MerryBot.Test`），不要改 public 面
- 测试覆盖重点是调度器（ClockService）、上下文压缩（AgentCompaction）、并发上限（AgentConcurrencyLimit）、配置注册（ConfigRegistry）、token 用量聚合（TokenUsageAggregatorTests）、流式解析（LlmBackendStreamTests）、流式 reset 重试（StrayToolCallRetryTests）、请求缓存（RequestCaching）、视觉路由（VisionRouter）、工具失败语义（ToolSetFailureTests）、浏览器探测（ChromeDetectionTests）

## 内置插件一览（当前代码中实际存在）

| 插件 Id | 类 | 说明 |
| --- | --- | --- |
| `agent` | `AgentPlugin` | 主 AI 机器人（群聊对话、工具调用、定时任务、记忆、技能） |
| `agent-service` | `AgentServicePlugin` | Skill/记忆/上下文快照 管理服务（供 agent 与 WebUI 复用同一实例；与 `agent` 共享数据库 scope） |
| `llm-provider` | `LlmProviderPlugin` | LLM Provider/模型/Key 管理（Background） |
| `view-version` | `ViewVersion` | `/version` `/update [-f]` `/reload`（授权用户专用，转发给 `IHostLifecycle`） |
| `auto-increase` | `AutoIncrease` | 刷屏自动 +1（Background） |
| `help` | `Help` | `/help` |
| `about` | `About` | `/about`（`isIgnore: true`，默认不加载） |
| `herui-saying` | `HeruiSaying` | `/hr` 锐言锐语（`isIgnore: true`） |

> 注意：`README.md` 中提到的"群刊（highlights）""终端（run-command）""MainPlugin"等插件在当前代码中已不存在，README 部分内容过时；以 `plugins/` 目录实际内容为准。

## 安全注意事项

- **LLM API Key**：写入数据库前用本机 DataProtection 密钥加密（密钥环在 `<data>/llm-provider-key-ring/`）；WebUI 只回显末四位与指纹，不回读原文。密钥环文件应妥善保护，丢失后已存 Key 无法解密
- **授权校验**：`/update`、`/reload` 等高危操作校验发送者 QQ == `AuthorizedUser`；WebUI 的更新接口同样受 `HostLifecycle` 互斥与授权约束
- **Shell 工具默认关闭**：`allow-shell` 未开启时 `TerminalToolSet` 不注册（模型无法执行 shell）；开启后仅 Linux 可用，且按 `shell-user` 指定的系统用户执行，需保证该用户权限受控
- **Shell 工具推荐搭配 `shell-user` 使用**：`bash`/`shell` 工具应始终与 `shell-user` 配置一同设置 —— 让命令以独立低权限系统用户身份执行，实现**用户隔离**（LLM 进程与 shell 进程的权限面分离），而不是以机器人自身用户或 root 运行。推荐为该用途创建专用用户（如 `bot-shell`），仅授予最小所需权限
- **WebUI 监听地址**：默认仅绑定 `localhost:5000`，通过 `<data>/setting.toml` 的 `web-address` 设置（不在 WebUI 内可改，避免引导问题）；远程管理请用 SSH 端口转发（无内置鉴权是设计决策，见"WebUI"一节，**不要**擅自为 WebUI 增加账号/登录体系）。若改为 `0.0.0.0`，须经受控内网或 HTTPS 反向代理访问（尤其 LLM 配置页会经浏览器提交 Key）
- **资源引用**：消息链中的图片/文件一律经 `merrybot://` 本地引用 + 本地 API 提供，前端不直连远端 URL，避免 SSRF/隐私外泄；下载资源受 `ResourceSizeLimitMb` 限制
- **插件隔离**：`PluginInitializer` 按插件隔离依赖解析失败（单个插件异常不影响其余插件加载）；插件数据库按 scope 隔离，`DropCollectionAsync` 只能删自己 scope 内的表
- **日志脱敏**：群消息日志只记录群号/发送者/消息链长度摘要，不落完整消息链
