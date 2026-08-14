

using BotPlugin;
using NapcatClient;
using NapcatClient.MessageType;

namespace MerryBot;

internal partial class Logic
{
    private void OnGroupMessage(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, ReceivedGroupMessage data)
    {
        foreach (var i in plugins)
        {
            if (!i.Instance.IsEnable)
            {
                //if the plugin is not enable, skip it
                continue;
            }
            var pluginChain = messageChain.Select(message => message.Clone()).ToList();
            _ = InvokePluginAsync(i, isMentioned, command, pluginChain, data);
        }
    }

    private async Task InvokePluginAsync(PluginInfo plugin, bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, ReceivedGroupMessage raw)
    {
        try
        {
            await plugin.Instance.OnGroupMessageAsync(isMentioned, command, messageChain, raw);
        }
        catch (Exception exception)
        {
            logger.Warn(exception, "插件消息处理失败: {0}", plugin.PluginTag.Id);
        }
    }
}
