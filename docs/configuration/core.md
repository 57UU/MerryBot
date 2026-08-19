---
title: 核心配置
parent: 配置说明
nav_order: 2
---

以下为 `Config` 类的字段及默认值：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `NapcatServer` | `ws://localhost:3001/` | Napcat WebSocket 服务地址 |
| `NapcatToken` | `napcat` | 连接 Napcat WebSocket 服务时使用的认证 Token |
| `QqGroups` | `[]` | 需要接收和处理消息的 QQ 群号列表 |
| `AuthorizedUser` | `-1` | 拥有管理权限的授权用户 QQ 号；部分高危操作（`/update`、`/reload` 等）会校验此 QQ 号 |
| `MachineCode` | `-1` | 历史记录使用的机器编号；小于 0 时首次启动自动生成 0–31 的编号 |
| `ResourceSizeLimitMb` | `20` | 下载并保存的图片/文件大小上限（MB） |
| `ReconnectIntervalSeconds` | `15` | 消息适配器断开后重试连接的间隔（秒） |
