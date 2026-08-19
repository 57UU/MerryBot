---
title: 插件子系统
parent: 框架核心
nav_order: 3
---

# 插件子系统

MerryBot 通过反射加载插件，插件之间独立隔离。所有内置插件位于 `plugins/` 目录（`RootNamespace` 为 `BotPlugin`）。

## 基础设施

| 文件 | 内容 |
| --- | --- |
| `_pluginBase.cs` | `Plugin` 抽象基类 |
| `_interface.cs` | `PluginInterop` / `PluginTag` / `PluginStorage` |
| `_common.cs` | `MessageContext` / `SessionKey` |
| `_interface.event.cs` | 事件注册接口 |

### Plugin 抽象基类

所有插件必须继承 `Plugin`：

```csharp
public abstract class Plugin : IDisposable
{
    public bool IsEnable { get; internal set; } = true; // 恒为 true（无停用机制）
    protected readonly IEnumerable<long> GroupId;        // 工作 QQ 群范围
    protected readonly ISimpleLogger Logger;             // 日志（统一日志体系）
    protected readonly PluginInterop Interop;            // 与宿主的互操作
    protected readonly MessageChannel Channel;           // 群消息发送通道

    public Plugin(PluginInterop interop) { ... }

    public virtual Task OnMessageAsync(bool isMentioned, Command? command,
        IReadOnlyList<TypedMessage> messageChain, MessageContext context);
    public virtual Task OnLoaded();
    public virtual void Dispose();
}
```

- `OnMessageAsync`：群消息入口（`isMentioned` 是否被 @、`command` 为 `/` 开头的命令、`messageChain` 为消息链、`context` 为发送者上下文）
- `IsEnable` 为 false 时 `OnMessageAsync` 永不被调用；当前无停用机制，恒为 true
- `OnLoaded`：插件加载完成回调；`Dispose`：卸载清理

### 互操作（`PluginInterop`）

`PluginInterop` 在构造时由宿主注入（`PluginInitializer` 负责 DI 与拓扑排序），向插件暴露：

- `Logger`（统一日志）、`GroupId`（群范围）、`Channel`（消息通道）
- 存储能力（`PluginStorage`：经 [存储](storage.html) 的 `PluginDatabaseScope` 隔离读写）

### 消息上下文（`MessageContext` / `SessionKey`）

- `SessionKey("qq", "group", groupId)`：会话唯一标识（Agent 会话、`ai_messages` 审计均以此键关联）
- `MessageContext`：携带发送者 QQ、昵称（优先群名片）、机器人自身 QQ

## 插件生命周期

```
发现（反射扫描 plugins 目录）
  → 实例化（PluginInitializer 拓扑排序 + 依赖注入 PluginInterop）
  → OnLoaded()（初始化）
  → 消息分发（宿主按插件逐个调用 OnMessageAsync，支持拦截器）
  → Dispose()（Shutdown 时卸载）
```

## 内置插件一览

| 插件 | 职责 |
| --- | --- |
| `AgentServicePlugin`（`Agent*.cs` 多文件） | **LLM Agent 插件**：对话循环、工具调用、定时任务、技能与记忆；内含 `DatabaseContextHistory`（`Agent.ContextHistory.cs`）持久化会话、`Agent.LogBridge.cs` 事件桥接、`ContextSnapshotService` 快照服务、`MemoryManagementService` |
| `About` | 版本/仓库信息 |
| `Help` | 帮助菜单 |
| `ViewVersion` | 查看版本 |
| `AutoIncrease` | 自动回复（简单关键字回复） |
| `HeruiSaying` | 贺瑞语录（示例/娱乐插件） |
| `LlmProviderPlugin` | LLM Provider / 模型 / Key 维护（加密存储，供 WebUI 与 Agent 使用） |
| `MessageService` | 消息服务插件（与宿主 `MessageService` 配合） |

> 插件开发入门见[插件开发](../plugin-development/index.html)，API 参考见[插件 API](../plugin-development/api.html)，存储用法见[存储与工具](../plugin-development/storage.html)。

## 相关页面

- [核心宿主](core.html) — 插件加载与消息分发
- [存储](storage.html) — 插件数据隔离（PluginDatabaseScope）
- [Agent 架构](../agent/index.html) — Agent 插件的内部设计
