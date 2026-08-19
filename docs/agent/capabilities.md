---
title: 能力管理
parent: Agent 架构
nav_order: 8
---

# 能力管理

`AgentPlugin` 在创建会话时组装工具；`AgentServicePlugin` 复用同一份 Skills、记忆和上下文快照服务给 Agent 与 WebUI。配置或 Skill 变化后执行 `/new` 会重建该会话并加载最新工具集。

## Skills

Skills 位于 `<data>/skills/`。可上传单个 `.md`，或包含唯一 `SKILL.md` 的目录型 ZIP；WebUI 支持查看、上传、禁用和删除。禁用时入口文件追加 `.disable`，模型的 `skill_list` 与 `skill_read` 不会看到它。

技能名称列表进入系统提示词；模型需显式调用 `skill_read` 读取内容。单次读取最多返回 20,000 个字符，避免占满上下文。

## 持久记忆与上下文

记忆按 `SessionKey` 隔离，存放在 `agent` scope。模型通过 `save_memory`、`recall_memory`、`query_memory` 和 `delete_memory` 操作；WebUI 可按会话维护记忆索引和具体条目。上下文快照只读展示当前会话状态，便于排查，不替代历史记录或记忆。

## 消息、视觉与网络

`MessageTool` 可读取回复、合并转发和群历史，并将 Markdown 渲染后发送。只有主模型具备 `ImageInput`，或配置了可用辅助视觉模型时，才注册 `load_image`；辅助模型按 `VisionLlmModels` 顺序降级。图片读取受 `MaxImageSizeMb` 限制。

`WebTools` 使用本地 Chrome/Edge 进行搜索与网页抓取；浏览器不可用时工具调用会返回错误，其他会话能力不受影响。

## 子任务与 Shell

子任务复用父会话的模型和工具，但不能再派生子任务；并发数由 `MaxSubagents` 限制。Shell 默认不注册，开启 `AllowShell` 后仅 Linux 可用，后台任务数由 `MaxBackgroundTasks` 限制。

Shell 命令具有宿主系统权限。应设置低权限 `ShellUser`；留空时命令以机器人进程身份执行。不要在不可信群或公网可访问的管理环境中开启它。

## 相关页面

- [插件配置](../configuration/plugins.html) — Agent 配置字段与默认值
- [Tool Design](tool-design.html) — 工具注入和执行语义
- [消息与 NapCat](../architecture/messages.html) — 本地资源引用
