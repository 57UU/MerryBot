

using BotPlugin;
using NapcatClient;
using NapcatClient.MessageType;

namespace MerryBot;

internal partial class Logic
{
    private void OnGroupMessage(bool isMentioned,Command? command, ReceivedGroupMessage data)
    {
        foreach (var i in plugins)
        {
            if (!i.Instance.IsEnable)
            {
                //if the plugin is not enable, skip it
                continue;
            }
            try
            {
                i.Instance.OnGroupMessage(isMentioned, command, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }
        }
    }
}