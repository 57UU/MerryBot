---
title: 配置说明
has_children: true
nav_order: 2
---

MerryBot 的配置主要存放在 LiteDB 数据库 `plugin_data.db` 中（数据目录由环境变量 `MERRY_BOT` 指定，未设置时默认为程序工作目录下的 `data` 文件夹）。另有少量启动必需项放在 `setting.toml` 文件中。

配置分为三类：

- **启动配置**：[`setting.toml`](startup.html) 中的启动必需项（目前只有 WebUI 监听地址），**不在 WebUI 中提供修改入口**——避免"WebUI 挂了就改不回来"的引导问题。修改后重启 MerryBot 生效。
- **核心配置**：连接、群组、机器编号等宿主级设置，由 `ConfigManager` 管理，存储于 `plugin_data.db` 的 `Plugin_Config_Table` 集合（键为 `core/config`）。
- **插件配置**：每个插件有独立的配置类型，按插件 Id 存储，互不干扰。首次启动时生成默认配置并落库。

核心配置与插件配置都可以在 WebUI「**配置中心**」（`/config`）中查看和修改，每个配置文件单独保存。修改后若页面提示需要重启才能生效，请使用侧边栏底部的「重启程序」按钮。

## WebUI 安全说明

WebUI 默认只监听 `http://localhost:5000`（由启动配置 `setting.toml` 的 `web-address` 控制），**没有登录和 API 鉴权**。配置保存、重启和更新接口也遵循这一边界。远程访问请使用 SSH 端口转发（`ssh -L 5000:localhost:5000 user@host`）；若改为 `0.0.0.0`，必须由受控内网或带鉴权的 HTTPS 反向代理保护。尤其「LLM 配置」页的 Key 虽不能读回，写入时仍会经浏览器提交。
