---
title: WebUI 子系统
parent: 框架核心
nav_order: 5
---

# WebUI 子系统

WebUI 是内嵌的 **Blazor 历史后台**（`MerryBot.WebUI/` 项目），用于查看消息记录、管理群组、维护 LLM Provider/模型/Key、编辑配置。它在主程序进程内运行，也支持独立启动（`Program.Main`）。

## 架构

- **渲染模式**：ASP.NET Core Blazor **InteractiveServer**，页面 C# 跑在 server，与宿主同进程
- **交互方式**：页面经 DI 拿 `WebUiServiceRegistry`（`Api/`）直接调用进程内服务，不经 HTTP；
  仅图片/文件/资源二进制下载保留 GET 端点（`Program.cs` 的 `/api/image/{id}`、`/api/file/{id}`、` /api/resource`，
  `<img>/<video>/<a download>` 必须用 URL），Skill 上传用 Blazor 内建 `InputFile` 直调服务
- **宿主方式**：`Program.CreateApp(historyRecorder, webAddress, ...)` 由主进程调用，注册空 `WebUiServiceRegistry`；
  宿主建完 core 服务后填充，`LoadPlugins` 后由 `RegisterWebUi` 填充插件服务（未加载为 null，页面显示“服务不可用”）；
  `webAddress` 来自 `setting.toml` 的启动配置（见[配置说明](../configuration/startup.html)）。
  独立启动（`Program.Main`）同样打开插件库并填充注册表的 core 部分（`LogFiles`、`PluginStorageDatabase`、`IContextSnapshotService`）
- **服务注册**：`HistoryRecorder`、`PluginStorageDatabase`、`IContextSnapshotService` 直接进 WebUI DI；
  其余经 `WebUiServiceRegistry`（解决“WebApp 建好时插件实例还不存在”的加载顺序问题）。
  大 JSON 经 SignalR 传输受 `MaximumReceiveMessageSize`（2MB）约束

## 服务分区（`Api/` 目录）

- **直连服务**（Blazor 页面 `@inject WebUiServiceRegistry` 调用）：`WebUiServiceRegistry`（注册表）、
  `ConfigRegistry`（配置中心）、`LogFileService`（日志浏览，按 `*.log` 枚举，向后多扫）、
  `GroupBrowseService`（群管理）、`DatabaseMaintenanceService`（数据库大小/Rebuild）、
  `LlmProviderService` + `ModelsDevCatalogService`（LLM Provider/模型/Key 与 models.dev 目录）、
  插件服务经注册表（Skill/记忆/提示词复写/会话控制/上下文快照/定时任务 `ClockService`）
- **保留的 HTTP 端点**：仅二进制 GET（见上），无业务 POST API（故无 `UseAntiforgery`）；
  `wwwroot/js/` 只剩纯 DOM/音频助手（滚动、语音播放、转发弹窗、`InputFile` 之外的下拉对齐）

页面位于 `Components/Pages/`：群消息、AI 消息、会话 AI 消息、LLM 配置、记忆、技能、统计、配置编辑、高级配置、日志、群管理、转发消息等。

## 安全模型

- **无内置账号体系或鉴权**：WebUI 默认仅监听 `localhost:5000`。配置、重启、重载和更新操作也不识别 QQ 身份；修改为 `0.0.0.0` 即会把管理能力暴露给可访问该端口的人
- **远程访问**：推荐 SSH 端口转发（`ssh -L 5000:localhost:5000 user@host`）。如必须经网络访问，应由受控内网或带鉴权的 HTTPS 反向代理保护
- **API Key 保护**：LLM API Key 用 DataProtection 加密存储（密钥环 `<data>/llm-provider-key-ring/`），WebUI 只回显末四位与指纹，不可读回
- **日志脱敏**：群消息日志只保留群号/发送者/链长摘要，避免完整消息链导致日志膨胀与隐私泄露

## 相关页面

- [核心宿主](core.html) — WebUI 在装配中的位置
- [存储](storage.html) — 数据库与高级配置面板
- [插件子系统](plugins.html) — LLM Provider 插件
- [配置说明](../configuration/index.html) — `setting.toml` 与核心配置
