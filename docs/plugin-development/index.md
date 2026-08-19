---
title: 插件开发
has_children: true
nav_order: 3
---

插件位于 `plugins/` 项目，宿主会从该程序集反射发现标记为 `[PluginTag]` 的类型。一个插件必须继承 `Plugin`，且只能有一个公开构造函数；构造函数必须接收 `PluginInterop`，其余参数只能是其他插件实例或 `IPluginConfig` 实现。

| 文档 | 内容 |
| --- | --- |
| [生命周期与配置](lifecycle.html) | 标签、构造注入、配置、`OnLoaded` 和卸载 |
| [示例与事件](example.html) | 最小消息插件与通知事件 |
| [API 参考](api.html) | `Plugin`、`PluginInterop` 和拦截器 |
| [存储与工具](storage.html) | 对象存储、scoped 数据库与日志 |

`PluginTag(id, name, description, isIgnore, type)` 中，`id` 是配置和数据库的隔离键；`isIgnore=true` 时不加载。`PluginType` 为 `Interactive`、`Background` 或 `Admin`，当前主要用于插件分类。
