---
title: 能力管理
parent: Agent 架构
nav_order: 8
---

# 能力管理

`AgentPlugin` 在创建会话时组装工具；`AgentServicePlugin` 复用同一份 Skills、记忆和上下文快照服务给 Agent 与 WebUI。配置变化后，可执行 `@机器人 /new` 重建该群会话并加载最新工具集。

## Skills

Skills 位于 `<data>/skills/`。可上传单个 `.md`，或包含唯一 `SKILL.md` 的目录型 ZIP；WebUI 支持查看、上传、禁用和删除。禁用时入口文件追加 `.disable`，模型的 `skill_list` 与 `skill_read` 不会看到它。

创建会话时，启用技能名称列表会进入系统提示词；模型需显式调用 `skill_read` 读取内容。`skill_list` 与 `skill_read` 每次调用都会刷新技能快照，因此新上传或切换状态的技能无需重启即可读取；单次读取最多返回 20,000 个字符。

## 持久记忆与上下文

记忆按 `SessionKey` 隔离，存放在 `agent` scope。模型通过 `save_memory`、`recall_memory`、`query_memory` 和 `delete_memory` 操作；WebUI 可按会话维护记忆索引和具体条目。上下文快照只读展示当前会话状态，便于排查，不替代历史记录或记忆。

## 消息、视觉与网络

`MessageTool` 通过统一的 `get_message` 读取普通消息和合并转发：参数必须原样填写消息文本中显示的 `merrybot://message/...` 或 `merrybot://forward/...` 内部引用，裸 ID 和外部 URL 会作为工具错误返回给模型；它也能读取群历史并将 Markdown 渲染后发送。只有主模型具备 `ImageInput`，或配置了可用辅助视觉模型时，才注册 `load_image`；辅助模型按 `VisionLlmModels` 顺序降级。图片读取受 `MaxImageSizeMb` 限制。

`WebTools` 使用本地 Chrome/Edge 进行搜索与网页抓取；浏览器不可用时工具调用会返回错误，其他会话能力不受影响。

## 子任务与 Shell

子任务复用父会话的模型和工具，但不能再派生子任务；并发数由 `MaxSubagents` 限制。Shell 默认不注册，开启 `AllowShell` 后仅 Linux 可用，后台任务数由 `MaxBackgroundTasks` 限制。

Shell 命令具有宿主系统权限。应设置低权限 `ShellUser`；留空时命令以机器人进程身份执行。不要在不可信群或公网可访问的管理环境中开启它。

## 相关页面

- [插件配置](../configuration/plugins.html) — Agent 配置字段与默认值
- [Tool Design](tool-design.html) — 工具注入和执行语义
- [消息与 NapCat](../architecture/messages.html) — 本地资源引用
