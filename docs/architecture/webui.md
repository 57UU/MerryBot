---
title: WebUI 子系统
parent: 框架核心
nav_order: 4
---

# WebUI 子系统

WebUI 是内嵌的 **Blazor 历史后台**（`MerryBot.WebUI/` 项目），用于查看消息记录、管理群组、维护 LLM Provider/模型/Key、编辑配置。它在主程序进程内运行，也支持独立启动（`Program.Main`）。

## 架构

- **渲染模式**：ASP.NET Core Blazor **InteractiveServer** + Minimal API
- **宿主方式**：`Program.CreateApp(historyRecorder, webAddress, ...)` 由主进程调用，注入 `IContextSnapshotService` 等共享服务；`webAddress` 来自 `setting.toml` 的启动配置（见[配置说明](../configuration/startup.html)）
- **服务注册**：上下文快照服务直接注册进 WebUI DI（组件注入读取），避免大 JSON 经 SignalR 传输超过 32KB 默认上限导致断连

## API 分区（`Api/` 目录）

每个功能区一个 Minimal API mapper，由宿主在装配时调用 `Map(app, ...)` 注册：

| Mapper | 功能 |
| --- | --- |
| `ConfigApiMapper` | 配置中心（核心 + 插件配置查看/修改） |
| `AdvancedConfigApiMapper` | 高级配置：原始 BSON 查看/删除插件数据库条目（排查残留数据） |
| `StatusApiMapper` | 概览页：连接状态、机器人 QQ、昵称、git 版本 |
| `GroupApiMapper` | 群管理 |
| `LogApiMapper` | 日志浏览（按 `*.log` 枚举） |
| `UpdateApiMapper` | 更新/重启/重载/退出 |
| `LlmProviderApiMapper` | LLM Provider / 模型 / Key 维护 |
| `SkillApiMapper` | 技能（Skills）管理 |
| `MemoryApiMapper` | 记忆管理 |
| `ContextSnapshotApiMapper` | 上下文快照 |
| `ConfigRegistry` | 配置注册表（`RegisterConfig("core", ...)` 等） |
| `ModelsDevCatalogService` | models.dev 模型目录查询 |

页面位于 `Components/Pages/`：群消息、AI 消息、会话 AI 消息、LLM 配置、记忆、技能、统计、配置编辑、高级配置、日志、群管理、转发消息等。

## 安全模型

- **无内置账号体系**：WebUI 默认仅监听 `localhost:5000`（`setting.toml` 的 `web-address`，修改 `0.0.0.0` 暴露公网风险自担）。远程访问推荐 SSH 端口转发（`ssh -L 5000:localhost:5000 user@host`），认证与加密交给 SSH
- **高危操作鉴权**：`/update`、`/reload` 等校验请求者 QQ == `AuthorizedUser`（核心配置字段）
- **API Key 保护**：LLM API Key 用 DataProtection 加密存储（密钥环 `<data>/llm-provider-key-ring/`），WebUI 只回显末四位与指纹，不可读回
- **日志脱敏**：群消息日志只保留群号/发送者/链长摘要，避免完整消息链导致日志膨胀与隐私泄露

## 相关页面

- [核心宿主](core.html) — WebUI 在装配中的位置
- [存储](storage.html) — 数据库与高级配置面板
- [插件子系统](plugins.html) — LLM Provider 插件
- [配置说明](../configuration/index.html) — `setting.toml` 与核心配置
