---
title: 生命周期
parent: 插件开发
nav_order: 1
---

# 生命周期

## 标签与构造函数

插件类必须继承 `Plugin` 并标记 `[PluginTag]`。只有一个公开构造函数；宿主可注入 `PluginInterop`、其他插件实例和 `IPluginConfig` 实现，依赖顺序由 `PluginInitializer` 自动计算。

```csharp
[PluginTag("example", "示例", "最小插件")]
public sealed class ExamplePlugin : Plugin
{
    public ExamplePlugin(PluginInterop interop)
        : base(interop)
    {
    }
}
```

需要运行时配置时，将 `IPluginConfig` 实现作为构造函数参数。配置加载、WebUI 自动生成和保存语义见[配置与 WebUI](configuration.html)。不要自行创建其他插件实例或数据库连接。

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
- [配置与 WebUI](configuration.html) — 自动配置与保存语义
- [示例与事件](example.html) — 消息回调
- [框架核心：插件子系统](../architecture/plugins.html) — 加载与隔离机制
