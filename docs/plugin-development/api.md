---
title: API 参考
parent: 插件开发
nav_order: 2
---

## 父类 API / 属性

以下成员在抽象父类 `Plugin` 中定义（`plugins/_pluginBase.cs`）：

| 成员 | 类型/签名 | 说明 |
|:---|:---|:---|
| `Logger` | `protected readonly ISimpleLogger` | 日志记录器，由 `PluginInterop` 注入 |
| `Interop` | `protected readonly PluginInterop` | 互操作性（查找插件、数据持久化、使用 Core 功能） |
| `Channel` | `protected readonly MessageChannel` | 消息发送通道（内含日志，失败不抛出） |
| `GroupId` | `protected readonly IEnumerable<long>` | 当前插件的工作范围（监听的 QQ 群列表） |
| `IsEnable` | `public bool { get; internal set; } = true` | 是否启用；为假时 `OnMessageAsync` 不会被调用（当前恒为 true） |
| `OnMessageAsync(...)` | `public virtual Task` | 收到新消息时的回调 |
| `OnLoaded()` | `public virtual Task` | 全部插件加载完成后执行，可放互操作性的初始化代码 |
| `Dispose()` | `public virtual void` | 插件卸载时的清理入口（`Plugin : IDisposable`） |

## 互操作性 - interop

`PluginInterop`（`plugins/_interface.cs`）在构造函数中注入，**不要在构造函数中使用互操作成员**（此时插件没有加载完），建议在 `OnLoaded` 函数中使用：

| 成员 | 类型/签名 | 说明 |
|:---|:---|:---|
| `FindPlugin<T>()` | `internal T?` | 查找类型为 T 的插件实例（返回 null 表示未找到）；推荐直接在构造函数中注入其他插件实例 |
| `PluginInfoGetter` | `PluginInfoGetter` | 获取所有插件的 `PluginInfo` |
| `PluginStorage` | `PluginStorage` | 插件存储（object 级读写），见「存储与工具」 |
| `ClockService` | `ClockService` | core 拥有的定时任务调度器（生命周期归宿主，插件不负责创建/释放） |
| `Interceptors` | `List<MessageInterceptor>` | 拦截器列表，拦截特定消息被本插件处理 |
| `Lifecycle` | `IHostLifecycle` | core 生命周期回调：检测更新 / 请求更新（fetch+merge+编译备用槽+切槽重启）/ 重启 / 重载 |
| `AuthorizedUser` | `long` | 授权用户的 QQ 号 |
| `MessageService` | `IMessageService` | 消息持久化与资源（图片/文件）读取 |
| `EventRegister` | `EventRegister` | 通知类事件订阅（`OnXxxReceived += handler`），见下 |

**定时任务执行器**：通过 `Interop.ClockService.Executor`（`DelegatingClockExecutor`）注册——Agent 插件初始化时执行 `Interop.ClockService.Executor.Inner = new AgentSessionClockExecutor(...)` 接管定时任务投递。

**Scoped 数据库**：`Interop.PluginStorage.PluginDatabaseScope` 提供当前插件隔离的 LiteDB 集合视图，见「存储与工具」。

## 事件订阅 - EventRegister

`Interop.EventRegister` 提供通知类事件的订阅入口，通过 `+=` 订阅、`-=` 退订，回调为同步执行：

```csharp
Interop.EventRegister.OnPokeEventReceived += OnPoke;
```

可用事件（`plugins/_interface.event.cs`）：`OnNoticeEventReceived`（基类，任意通知触发）、`OnGroupUploadEventReceived`、`OnGroupAdminEventReceived`、`OnGroupDecreaseEventReceived`、`OnGroupIncreaseEventReceived`、`OnGroupBanEventReceived`、`OnFriendAddEventReceived`、`OnGroupRecallEventReceived`、`OnFriendRecallEventReceived`、`OnPokeEventReceived`、`OnLuckyKingEventReceived`、`OnHonorEventReceived`、`OnGroupMsgEmojiLikeEventReceived`、`OnEssenceEventReceived`、`OnGroupCardEventReceived`。

## 拦截器 - Interceptors

方法签名：

```csharp
public delegate bool MessageInterceptor(MessageContext context)
```

返回 true 拦截，false 不拦截。
