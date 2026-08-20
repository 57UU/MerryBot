---
title: 日志与数据
parent: 运行维护
nav_order: 3
---

# 日志与数据

## 日志

日志保存到 `<data>/log/bot-<日期>.log`，按天和 10 MB 归档，最多保留 30 份。WebUI「日志」页可筛选级别和关键字、切换历史文件；群消息日志只记录群号、发送者和消息链长度，不记录完整内容。

## 数据维护

备份前先正常停止进程，再整体复制数据目录，至少包含：

- `plugin_data.db`
- `group_history.db` 与 `storage/`
- `llm-provider-key-ring/`
- `skills/` 与 `setting.toml`

DataProtection 密钥环丢失后，已保存的 LLM Key 无法解密。恢复时应保持数据库与密钥环来自同一备份；不要在运行中直接替换 LiteDB 文件。

## WebUI 访问边界

WebUI 默认绑定 `localhost:5000`，没有账号、会话或 API 鉴权。推荐使用 SSH 隧道进行访问：

```bash
ssh -L 5000:localhost:5000 user@host
```

若自行改为监听外网地址，必须在外层提供受控网络或带鉴权的 HTTPS 反向代理；否则模型 Key、配置、重启和更新能力都会暴露。

## 相关页面

- [启动配置](../configuration/startup.html) — 修改 WebUI 监听地址
- [WebUI 子系统](../architecture/webui.html) — API 与安全模型
- [更新与重启](maintenance.html) — A/B 双槽和发布流程
