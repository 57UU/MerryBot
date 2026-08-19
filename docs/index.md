---
title: MerryBot
nav_order: 0
---

# MerryBot

基于 **NapCat** 的 QQ 机器人框架，使用 **C#（.NET 10）** 编写，支持插件化开发。

主程序通过 WebSocket 连接 NapCat（OneBot 协议实现），监听 QQ 群消息并分发到内置插件；内置了基于 LLM 的 AI 机器人插件（Agent），支持工具调用、定时任务、技能与记忆系统；同时内嵌一个 Blazor WebUI 历史后台。

## 主要特性

- **插件化宿主**：按插件 Id 隔离配置和数据，按依赖顺序加载。
- **本地 WebUI**：查看消息与日志，维护群组、配置、模型、技能和记忆。
- **LLM Agent**：支持 function calling、并发工具、上下文压缩、子任务、持久记忆和视觉降级。
- **会话定时任务**：Linux 五字段 cron、持久化、超时和漏跑跳过。
- **本地资源模型**：图片和文件统一转换为 `merrybot://` 引用，避免前端直连远端地址。


## 下一步

- [快速开始](quickstart.html) — 安装、连接 NapCat、配置首个模型
- [运行维护](operations/index.html) — 部署、更新、日志与数据
- [框架核心](architecture/index.html) — 宿主、消息、插件和 WebUI
- [Agent 架构](agent/index.html) — LLM 请求、工具、会话和定时任务

## 相关链接


- [GitHub 仓库](https://github.com/57UU/MerryBot)
