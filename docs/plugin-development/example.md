---
title: 示例与事件
parent: 插件开发
nav_order: 1
---

## 示例

插件通过构造函数接收 `PluginInterop`；消息、资源和历史记录均由 Core 提供。以下为真实插件 `About` 的完整代码（`plugins/About.cs`）：

```csharp
[PluginTag("about", "About", "使用 /about 来查看关于", isIgnore: true)]
public class About : Plugin
{
    private const string aboutMessage=
"""
# -------About-------

Merry Bot

本程序的目的是实现QQ机器人的模块化开发，以插件的形式增加功能

访问Github仓库 https://github.com/57UU/MerryBot 以获取更多信息
""";

    public About(PluginInterop interop) : base(interop)
    {
        Logger.Info("about plugin start");
    }
    public override Task OnMessageAsync(
        bool isMentioned,
        Command? command,
        IReadOnlyList<TypedMessage> messageChain,
        MessageContext context)
    {
        if (command?.Name == "about")
        {
            _ = Channel.SendMessage(context.Session, aboutMessage);
        }
        return Task.CompletedTask;
    }
}
```

更多示例请查看 `plugins` 目录下的文件。

## 事件

| 函数 | 描述 |
| --- | --- |
| `OnMessageAsync` 函数 | 当收到新消息时，此函数会被调用 |
| `OnLoaded` 函数 | 当插件全部被加载完后会执行的函数，可以放一些互操作性的初始化代码。 |

### 消息处理链

插件通过异步回调获得处理后的消息链和轻量的消息上下文（平台无关）：

```csharp
public override Task OnMessageAsync(
    bool isMentioned,
    Command? command,
    IReadOnlyList<TypedMessage> messageChain,
    MessageContext context)
{
    // messageChain 中的 Reply、Forward、图片、文件等均为 merrybot:// 本地引用。
    // context 提供会话定位（Session）与发送者/机器人身份（SenderId/SelfId）。
    return Task.CompletedTask;
}
```

使用 `Interop.MessageService` 可按本地引用读取 Reply、Forward 或媒体资源；Core 会复用正在进行的请求并负责持久化。
