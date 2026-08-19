---
title: 快速开始
nav_order: 1
---

从零开始把 MerryBot 跑起来并接入 QQ 群，大约需要 15 分钟。

## 环境要求

- 一台 Linux 服务器（推荐）或 Windows 机器
- [.NET 10](https://dotnet.microsoft.com/) SDK 或运行时
- [NapCat](https://napcat.napneko.icu/)（QQ 机器人协议端，负责与 QQ 通信）
- 一个用于机器人的 QQ 号
- Chrome / Edge 浏览器（可选，网页搜索、Markdown 渲染等网络类工具需要，可用 `CHROME_BIN` 环境变量指定路径）

## 第 1 步：安装 NapCat

1. 下载并启动 NapCat，使用其 QQ 扫码登录机器人账号
2. 配置 **WebSocket 服务**并启动，记下**地址** 与 **Token**


## 第 2 步：获取并构建 MerryBot

```bash
git clone https://github.com/57UU/MerryBot.git
cd MerryBot
./launch.sh
```

`launch.sh` 首次运行会自动执行 `build.sh` 完成编译并启动程序。

## 第 3 步：启动并配置核心连接

1. 程序启动后自动创建数据目录（默认工作目录下的 `data/`，可用环境变量 `MERRY_BOT` 指定其他位置），并生成启动配置文件 `<data>/setting.toml`
2. 浏览器打开 WebUI：**http://localhost:5000**（监听地址由 `<data>/setting.toml` 的 `web-address` 控制，默认仅绑定本机，远程访问推荐使用SSH隧道转发避免安全风险）
3. 进入「**配置中心**」，填写核心配置：

   | 配置项           | 说明                                                                |
   | ---------------- | ------------------------------------------------------------------- |
   | `NapcatServer`   | NapCat WebSocket 地址                                               |
   | `NapcatToken`    | NapCat 认证 Token                                                   |
   | `QqGroups`       | 要监听的 QQ 群号列表（可以转到'群聊管理页面'页面可视化编辑）        |
   | `AuthorizedUser` | 授权用户 QQ 号（Bot 管理员，`/update`、`/reload` 等高危操作会校验） |

4. 保存后按提示使用侧边栏底部的「**重载程序**」按钮重启生效

> 核心配置存储在数据库 `plugin_data.db` 中，`setting.toml` 只保留启动必需项（目前仅 `web-address`）。

## 第 4 步：配置 AI 机器人（Agent）

### 配置 LLM 服务
在 WebUI「**LLM 配置**」页（`/llmproviders`）：

1. 搜索并选择一个 models.dev Provider（目录会本地缓存，点击「刷新 models.dev」才联网更新）
2. 添加要使用的模型，填入 OpenAI Chat Completions 兼容的 API 地址与 Key（Key 加密存储，仅回显末四位，不可读回）

### 配置 Agent 插件
打开配置中心-> Agent:

可以设置主模型、外挂视觉模型、shell等功能。

## 第 5 步：在群里验证

1. 进入 3 步配置的 QQ 群
2. 在群里 **@机器人** 发一句话，例如"你好"
3. 收到回复即部署成功

常用命令：

- `@机器人 /new [内容]` 或 `#新对话` — 清空上下文开新对话
- `@机器人 /compact [主题]` — 手动压缩上下文
- `/help` — 查看已加载插件
- `/version` — 查看当前版本

## 常见问题

**程序启动时报 NapCat 连接失败？**
不影响启动。程序不再同步等待登录信息，NapCat 未启动也能运行；NapCat 就绪后会自动按 `ReconnectIntervalSeconds`（默认 15 秒）重连。

**Shell 终端工具不可用？**
`AllowShell` 默认关闭（安全考虑）。需在 Agent 插件配置中开启；开启后命令以 `ShellUser` 指定的 Linux 用户（`sudo -u user`）执行，留空则以机器人进程所属用户执行。仅 Linux 生效。

**如何远程访问 WebUI？**
WebUI 无内置账号体系，默认只监听本机。远程访问请使用 SSH 端口转发：

```bash
ssh -L 5000:localhost:5000 user@host
```

**网页搜索 / Markdown 渲染报错？**
网络类工具通过 Selenium 操纵 Chrome/Chromium 实现。请安装 Chrome，或在环境变量 `CHROME_BIN` 中指定浏览器路径。

**Markdown 渲染中文不显示？**

命令行环境还需安装中文字体（如 `fonts-noto-cjk`、`fonts-noto-color-emoji`）。

```bash
sudo apt-get update
# 安装彩色 Emoji 字体
sudo apt-get install -y fonts-noto-color-emoji
# 安装 Google 诺托字体
sudo apt-get install -y fonts-noto-cjk
# 其他生僻字 optional
sudo apt install fonts-noto-cjk-extra fonts-hanazono
# 刷新字体缓存
sudo fc-cache -fv
```

**如何更新版本？**
管理员在群里执行 `@机器人 /update`，或直接使用 WebUI 左下角的「更新版本」按钮。
