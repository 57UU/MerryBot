---
title: 配置与 WebUI
parent: 插件开发
nav_order: 2
---

# 配置与 WebUI

插件配置类型实现 `IPluginConfig`，有无参构造函数，并以公开可读写属性声明字段。把它作为插件构造函数参数后，`PluginInitializer` 会按 `[PluginTag]` 的 `id` 自动解析：

1. 从 `plugin_data.db` 的 `Plugin_Config_Table` 读取配置。
2. 配置不存在时创建默认实例并立即保存。
3. 将同一实例注入插件构造函数，并注册到 WebUI。

已存配置的运行时类型与声明类型不匹配时，宿主只在本次启动使用内存默认值并记录警告，**不会覆盖**原有数据。

## 声明配置

```csharp
[ConfigDescription("示例插件", "用于演示 WebUI 自动配置。")]
public sealed class ExampleConfig : IPluginConfig
{
    [ConfigDescription("启用功能", "关闭后插件仅保留基础行为。")]
    public bool Enabled { get; set; } = true;

    [ConfigDescription("关键词", "每行一个关键词。")]
    public List<string> Keywords { get; set; } = [];
}

[PluginTag("example", "示例", "最小配置插件")]
public sealed class ExamplePlugin : Plugin
{
    private readonly ExampleConfig config;

    public ExamplePlugin(PluginInterop interop, ExampleConfig config)
        : base(interop)
    {
        this.config = config;
    }
}
```

`ConfigDescription` 的第一个参数是 WebUI 显示名称，第二个参数是说明；未标注时，WebUI 使用类型或属性名。

## 自动生成 WebUI 表单

插件无需编写 Razor 组件或 Minimal API。自动注册后的配置区 Id 为 `plugin:<插件 id>`；`ConfigRegistry` 通过反射读取类型和属性的描述、字段类型和值。

- 可编辑字段必须是公开实例属性、可读写且非索引器。
- 支持字符串、布尔、数值、枚举，以及这些标量类型的 `List<T>`；不支持的类型不会出现在 WebUI，并会写警告日志。
- WebUI 保存时先完成全部字段转换，再修改内存实例并写入数据库；持久化失败会恢复原值，避免半更新。

## 生效时机

保存配置不会自动重建插件。插件是否立即读取新值取决于自身实现；涉及会话工具或初始化资源时，应提示用户执行 `/new`、重载或重启。核心配置由宿主单独注册，插件只需使用构造函数注入模式。

## 相关页面

- [生命周期](lifecycle.html) — 插件创建与 `OnLoaded`
- [配置说明](../configuration/index.html) — 用户侧配置入口与安全边界
- [API 参考](api.html) — `PluginInterop` 与存储能力
