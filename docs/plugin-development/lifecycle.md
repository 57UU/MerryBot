---
title: 生命周期与配置
parent: 插件开发
nav_order: 1
---

# 生命周期与配置

## 标签与构造函数

插件类必须继承 `Plugin` 并标记 `[PluginTag]`。只有一个公开构造函数；宿主可注入 `PluginInterop`、其他插件实例和 `IPluginConfig` 实现，依赖顺序由 `PluginInitializer` 自动计算。

```csharp
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

public sealed class ExampleConfig : IPluginConfig
{
    public bool Enabled { get; set; } = true;
}
```

配置按插件 Id 自动加载；首次缺失时会创建默认实例并保存。不要自行创建其他插件实例或数据库连接，直接把依赖写入构造函数参数。

## 生命周期

1. 宿主发现标签并创建 `PluginInterop`、配置和依赖图。
2. 依赖满足后调用构造函数。
3. 全部可用插件加入列表后调用 `OnLoaded()`。
4. 每条已监听群消息调用 `OnMessageAsync(...)`；`IsEnable=false` 时跳过。
5. 关闭时按依赖逆序调用 `Dispose()`。

构造函数只保存依赖和做轻量初始化。依赖其他插件、注册互操作回调或启动后台工作应放在 `OnLoaded()`；否则可能看到尚未加载完成的插件。

## 不可用与失败

当前平台不支持等预期情况，在构造函数中抛出 `PluginNotUsableException`。宿主记录警告并跳过该插件。其他构造、依赖或消息处理异常也会被记录，不中断其他插件的加载和消息分发。

## 相关页面

- [API 参考](api.html) — 宿主提供的能力
- [示例与事件](example.html) — 消息回调
- [框架核心：插件子系统](../architecture/plugins.html) — 加载与隔离机制
