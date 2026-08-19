---
title: 插件子系统
parent: 框架核心
nav_order: 4
---

# 插件子系统

MerryBot 从 `Plugin` 所在程序集反射发现带 `[PluginTag]` 的类型。每个插件有独立的配置和数据 scope；单个插件的构造或依赖失败不会阻止其他插件加载。

## 基础设施

| 文件 | 内容 |
| --- | --- |
| `_pluginBase.cs` | `Plugin` 抽象基类和消息生命周期 |
| `_interface.cs` | `PluginTag`、`PluginInterop`、`PluginStorage`、命令和拦截器 |
| `_common.cs` | `MessageContext`、`SessionKey` |
| `_interface.event.cs` | OneBot 通知事件订阅 |

`Plugin` 暴露 `Logger`、`GroupId`、`Interop` 和 `Channel`。`OnMessageAsync` 接收 @ 状态、已解析的 `/` 命令、克隆后的消息链和 `MessageContext`；`OnLoaded` 用于依赖其他插件的初始化；`Dispose` 在关闭时按依赖逆序调用。当前没有运行时停用入口，`IsEnable` 默认始终为 `true`。

## 互操作与生命周期

`PluginInterop` 由宿主构造，提供日志、群范围、消息通道、消息服务、事件注册、生命周期服务、时钟服务、数据目录和授权 QQ。`PluginStorage` 提供对象读写及当前插件的 `PluginDatabaseScope`。

```
发现 → 构造依赖图 → 拓扑实例化 → OnLoaded → 消息分发 → 逆序 Dispose
```

构造函数可注入 `PluginInterop`、其他插件实例和 `IPluginConfig` 实现。不要在构造函数中调用依赖其他插件的互操作能力；所有插件加入列表后才会触发 `OnLoaded`。

## 内置插件

| Id | 类型 | 职责 |
| --- | --- | --- |
| `agent` | `AgentPlugin` | 接收 @ 群消息，维护 Agent 会话、工具和限流 |
| `agent-service` | `AgentServicePlugin` | 向 Agent 与 WebUI 提供 Skills、记忆和上下文快照；与 `agent` 共享数据 scope |
| `llm-provider` | `LlmProviderPlugin` | Provider、模型和加密 API Key 管理 |
| `view-version` | `ViewVersion` | `/version`、`/update`、`/reload` |
| `auto-increase` | `AutoIncrease` | 刷屏自动 +1（后台插件） |
| `help` | `Help` | `/help` |
| `about`、`herui-saying` | `About`、`HeruiSaying` | 默认忽略的示例/娱乐插件 |

> 插件开发入门见[插件开发](../plugin-development/index.html)，API 参考见[插件 API](../plugin-development/api.html)，存储用法见[存储与工具](../plugin-development/storage.html)。

## 相关页面

- [核心宿主](core.html) — 插件加载与消息分发
- [消息与 NapCat](messages.html) — `OnMessageAsync` 的输入来源
- [存储](storage.html) — 插件数据隔离（PluginDatabaseScope）
- [Agent 架构](../agent/index.html) — Agent 插件的内部设计
