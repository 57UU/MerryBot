# MerryBot

MerryBot 是基于 [NapCat](https://napcat.napneko.icu/) 上游的 QQ 机器人框架，使用 C#（.NET 10）编写，支持插件化开发。

主程序通过 WebSocket 连接 NapCat（OneBot 协议实现），监听 QQ 群消息并分发到内置插件；内置基于 LLM 的 AI 机器人插件（Agent），支持工具调用、定时任务、技能与记忆系统；内嵌 Blazor WebUI 历史后台。



## 文档
请参阅 docs/ 或 [项目文档站](https://57UU.github.io/MerryBot/)

## 快速开始

1. 安装 [NapCat](https://napcat.napneko.icu/) 并启动 WebSocket 服务
2. 获取源码并构建：`git clone https://github.com/57UU/MerryBot.git && cd MerryBot && ./launch.sh`
3. 打开 WebUI **http://localhost:5000**，在「配置中心」填写 NapCat 地址、Token、监听群号，重启生效
4. 在「LLM 配置」页添加模型与 API Key，设为默认模型
5. 在群里 **@机器人** 说话，收到回复即成功

## 内置插件

| 插件 | 说明 |
| --- | --- |
| **AI 机器人（Agent）** | 群聊 AI：function calling、定时任务、技能（Skills）、持久记忆、子任务 |
| **LLM Provider** | models.dev 目录、模型与 API Key 管理（WebUI「LLM 配置」页） |
| **ViewVersion** | `/version`、`/update`、`/reload` 版本管理与更新 |
| **AutoIncrease** | 刷屏消息自动 +1 |
| **Help / About / HeruiSaying** | `/help`、`/about`、`/hr` |

Agent 内置工具：消息读取（转发 / 回复 / 群历史）、图片查看、网页搜索与抓取、待办清单、Markdown 渲染为图片发送（支持 LaTeX / Mermaid）；`AllowShell` 开启后额外提供 Linux Shell 终端（前台 / 后台两种模式）。

## 配置

程序产生的数据保存在数据目录（环境变量 `MERRY_BOT` 指定，默认工作目录下 `data/`）：日志文件、配置文件、插件存储。

- **启动配置** `<data>/setting.toml`：目前仅 `web-address`（WebUI 监听地址，默认 `http://localhost:5000`，仅绑定本机）
- **核心配置**（NapCat 地址 / Token、监听群号、授权用户等）与**插件配置**：存储在数据库 `plugin_data.db`，通过 WebUI「配置中心」（`/config`）维护

## 整体架构

![Architecture](arch.svg)

## 插件开发

编写插件需继承 `Plugin` 抽象类、构造函数注入 `PluginInterop`，并以 `PluginTag` 属性标记；主程序通过反射自动加载 `plugins` 项目下的插件类。详细 API 与示例见 [插件开发](docs/plugin-development/)。
