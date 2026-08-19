---
title: 部署与运行
parent: 运行维护
nav_order: 1
---

# 部署与运行

## Linux 服务器

仓库的 `launch.sh` 是 Linux 的推荐入口：首次启动会将程序发布到 `build/slot_a`，随后运行该槽内的 `MerryBot`。

```bash
./launch.sh
```

`./launch.sh -f` 强制重建当前活动槽。`build.sh <target_dir>` 只供脚本或维护人员调用：它会清理目标目录、按当前架构发布 `linux-x64` 或 `linux-arm64` 版本，并复制 WebUI 静态文件。

脚本依赖 Bash 和 .NET 10 SDK；发布产物为 framework-dependent，目标机器仍需 .NET 10 运行时。

## 开发运行

Windows 或本地调试不使用 `launch.sh`，可直接运行主项目：

```powershell
dotnet run --project MerryBot/MerryBot.csproj
```

完整构建与单元测试命令见仓库根目录 `AGENTS.md`；浏览器工具需要本机 Chrome 或 Edge，可通过 `CHROME_BIN` 指定路径。

## 数据目录

`MERRY_BOT` 环境变量指定数据目录；未设置时使用当前工作目录下的 `data/`。首次启动会创建目录、日志目录和 `setting.toml`。

| 路径 | 用途 |
| --- | --- |
| `plugin_data.db` | 核心/插件配置、插件作用域数据、LLM 配置、Agent 记忆与定时任务 |
| `group_history.db` 与 `storage/` | 消息历史、事件、媒体对象和 AI 审计 |
| `log/` | NLog 文件 |
| `skills/` | Agent Skills |
| `setting.toml` | WebUI 监听地址 |

## 连接 NapCat

启动时 NapCat 不可用不会阻止进程运行。宿主按 `ReconnectIntervalSeconds` 检查连接并重试；先在 WebUI「配置中心」保存 `NapcatServer`、`NapcatToken` 和 `QqGroups`，再重启或重载程序。

## 相关页面

- [快速开始](../quickstart.html) — 首次连接和模型配置
- [核心配置](../configuration/core.html) — 连接与资源限制
- [更新、日志与数据](maintenance.html) — 维护操作
