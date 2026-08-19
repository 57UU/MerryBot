---
title: MerryBot
nav_order: 0
---

# MerryBot

基于 **NapCat** 的 QQ 机器人框架，使用 **C#（.NET 10）** 编写，支持插件化开发。

主程序通过 WebSocket 连接 NapCat（OneBot 协议实现），监听 QQ 群消息并分发到内置插件；内置了基于 LLM 的 AI 机器人插件（Agent），支持工具调用、定时任务、技能与记忆系统；同时内嵌一个 Blazor WebUI 历史后台。

## 主要特性

- **插件化架构**：按插件 Id 隔离配置与存储，反射加载，插件间独立
- **LLM Agent**：function calling、并发工具调用、上下文压缩、子任务、持久记忆
- **技能系统（Skills）**：数据目录下创建 `skills/` 文件夹即可被 AI 自动识别
- **定时任务**：Linux 五字段 cron，会话隔离，支持 misfire 跳过与超时
- **Shell 工具**：同步 / 异步两种模式，独立 Terminal 实例支持真并行
- **WebUI 历史后台**：查看消息记录、管理群组、维护 LLM Provider / 模型 / Key
- **消息工具**：Bing 搜索、网页浏览、Markdown 渲染为图片发送（支持 LaTeX / Mermaid）


## 下一步
完整的安装、配置与验证步骤见 [快速开始](quickstart.html)。

## 相关链接


- [GitHub 仓库](https://github.com/57UU/MerryBot)
- [README（部署与配置说明）](https://github.com/57UU/MerryBot/blob/master/README.md)
- [配置说明](Configuration.html)
