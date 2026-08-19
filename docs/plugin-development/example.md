---
title: 示例与事件
parent: 插件开发
nav_order: 2
---

## 示例

插件通过构造函数接收 `PluginInterop`；消息、资源和历史记录均由宿主提供。下面是最小命令插件：

```csharp
[PluginTag("greeting", "问候", "使用 /hello 获取问候")]
public sealed class GreetingPlugin : Plugin
{
    public GreetingPlugin(PluginInterop interop) : base(interop)
    {
    }

    public override Task OnMessageAsync(
        bool isMentioned,
        Command? command,
        IReadOnlyList<TypedMessage> messageChain,
        MessageContext context)
    {
        if (command?.Name == "hello")
        {
            _ = Channel.SendMessage(context.Session, "你好。");
        }
        return Task.CompletedTask;
    }
}
```

`Channel.SendMessage` 使用当前会话发送消息。若只处理 @ 机器人的指令，请先检查 `isMentioned`；普通插件仍会收到已监听群的所有消息。

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

使用 `Interop.MessageService` 可按本地引用读取 Reply、Forward 或媒体资源；宿主会复用正在进行的请求并负责持久化。详情见[消息与 NapCat](../architecture/messages.html)。
