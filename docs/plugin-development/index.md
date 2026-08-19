---
title: 插件开发
has_children: true
nav_order: 3
---

编写一个插件需要满足以下条件：

1. 一个插件应当放在 `plugins` 项目的一个文件中
2. 应当继承于 `Plugin` 抽象类
3. 有且只有一个构造函数，存在类型为 `PluginInterop` 的参数；插件之间不再依赖消息记录器或存储管理器
4. 在类前面使用属性 `PluginTag(string id, string name, string description, [bool isIgnore=false], [PluginType type=PluginType.Interactive])`

主程序会通过反射加载 `plugins` 项目下的所有插件类，因此需要满足上述条件。

## PluginTag 类属性标签

构造函数为 `(string id, string name, string description, bool isIgnore=false, PluginType type=PluginType.Interactive)`

参数说明：

- `id` - 插件标识符（英文），用于配置文件命名空间隔离
- `name` - 插件名称（可中文），用于显示
- `description` - 插件描述
- `isIgnore` - 是否忽略加载
- `type` - 插件类型

当 `isIgnore==true` 时，插件不会被加载。

`PluginType` 可选值：

- `Interactive` - 交互式插件（默认）
- `Background` - 后台插件
- `Admin` - 管理员插件

## Note

如果插件不可用（如不支持当前平台），请在构造函数中抛出 `PluginNotUsableException` 异常。
