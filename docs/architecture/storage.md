---
title: 存储
parent: 框架核心
nav_order: 3
---

# 存储

MerryBot 有两套独立的存储体系，都基于 **LiteDB**（本地 NoSQL，无需外部服务）：

| 存储 | 文件 | 用途 | 数据目录 |
| --- | --- | --- | --- |
| 插件存储 | `plugin_data.db` | 核心配置、插件配置、插件数据 | `<data>/` |
| 历史记录 | `group_history.db` + `storage/` | 群消息、图片/文件、事件、AI 消息审计 | `<data>/`（`botClient.PathPrefix`） |

两库分工明确：**插件存储**面向配置与键值数据（小而频繁），**历史记录**面向消息流水（大而顺序）。

## 插件存储（`DataProvider` 项目）

### PluginStorageDatabase

`PluginStorageDatabase`（LiteDB.Async 封装）管理 `plugin_data.db`，含两个物理集合：

| 集合 | 键格式 | 内容 |
| --- | --- | --- |
| `Plugin_Data_Table` | `{prefix}/{pluginId}` | 插件对象数据（`StorePluginData` / `GetPluginData`） |
| `Plugin_Config_Table` | `{prefix}/{pluginId}` | 插件配置（`SetPluginConfig` / `GetPluginConfig`） |

关键设计：

- **前缀命名空间**：`prefix` 参数区分归属，默认 `"plugin"` 保持插件数据兼容；核心使用 `prefix: "core"`（如核心配置键为 `core/config`，时钟存储键为 `core/clock`）
- **旧键兼容**：带前缀键不存在时回退读取无前缀旧键，下次保存时迁移到规范键，平滑升级存量数据
- **原始 BSON 读取**：`GetRawDataEntriesAsync` / `GetRawConfigEntriesAsync` 以 `BsonDocument` 返回全部条目，避免已删除插件类型缺失导致强类型反序列化抛 `LiteException`；供 WebUI「高级配置」面板排查残留数据
- **schema 迁移**：`PluginStorageDatabase.Migrations.cs` 提供幂等迁移（`MigrateAsync`），启动时执行，当前版本 1

### PluginDatabaseScope

`CreateScope(pluginId, prefix = "plugin")` 返回按插件隔离的**集合视图**（`PluginDatabaseScope`）：只允许访问该插件名下的集合，防止插件越界读写其他插件的数据。

核心用法示例：`PluginStorageDatabase.CreateScope("clock", prefix: "core")`（时钟存储）、`CreateScope("agent")`（Agent 上下文快照、记忆）。`agent-service` 有意复用 `agent` scope，避免管理服务与 Agent 看到不同的数据。

## 历史记录（`DataService` 项目）

### HistoryRecorder

`HistoryRecorder` 管理 `group_history.db`，集合按用途划分：

| 集合 | 内容 |
| --- | --- |
| `messages` | 群消息（GroupId/SenderId/MessageId/Time 索引） |
| `images` / `files` | 图片床 / 文件床（Hash 唯一索引，幂等去重） |
| `events` | 群事件（进群/退群/禁言等） |
| `forward_messages` | 合并转发消息 |
| `group_names` | 群名历史 |
| `ai_messages` | **AI 消息审计**（按 SessionKey 索引，Agent 对话留痕） |
| `resource_references` | `merrybot://` 资源引用（消息与对象的映射） |

设计要点：

- **对象存储分离**：图片/文件等二进制对象存入 `storage/` 目录（`FileSystemObjectStorage`，bucket 为 `images` / `files`），LiteDB 只存元数据与哈希引用，避免数据库膨胀
- **雪花 ID**：`IdGen.IdGenerator`（机器码 `MachineCode` 0–31，由核心配置生成并落库），保证跨进程时间有序
- **索引容错**：索引创建失败只记日志不抛异常（历史数据问题不导致启动失败）；Hash 唯一索引已有重复数据时创建失败，由写入侧的幂等兜底
- 消息入库通过 `MessageService` 完成（同时负责 `merrybot://` 资源引用与 AI 消息审计）

## 存储与 Agent 的关系

- Agent 插件与宿主共享 `agent` scope（`ContextSnapshotService` 等），有明确注释说明为何共用
- Agent 对话通过 `ai_messages` 集合做消息审计（`MessageService`），用户/助手/工具消息各留一条记录
- LLM Provider / Key 的存储与加密由 `LlmProviderPlugin` 管理（见 [WebUI 子系统](webui.html)）

## 相关页面

- [核心宿主](core.html) — 装配顺序与 ConfigManager
- [插件子系统](plugins.html) — 插件如何获得存储能力
- [配置说明](../configuration/index.html) — 配置存放位置
