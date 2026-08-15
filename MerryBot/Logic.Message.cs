

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

            if (IsInterceptorActive(i, data))
            {
                continue;
            }
            var pluginChain = messageChain.Select(message => message.Clone()).ToList();
            _ = InvokePluginAsync(i, isMentioned, command, pluginChain, data);
        }
    }

    /// <summary>逐个执行插件的拦截器；单个拦截器抛异常时记录日志并继续，不中断整条拦截链与消息分发。</summary>
    private bool IsInterceptorActive(PluginInfo plugin, ReceivedGroupMessage data)
    {
        foreach (var interceptor in plugin.Interop.Interceptors)
        {
            try
            {
                if (interceptor(data))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "插件拦截器执行失败: {0}", plugin.PluginTag.Id);
            }
        }
        return false;
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
