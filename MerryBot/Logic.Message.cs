

using NapcatClient;
using NapcatClient.MessageType;

namespace MerryBot;

internal partial class Logic
{
    private void OnGroupMessageMentioned(long groupId, ReadOnlySpan<TypedMessage> chain, ReceivedGroupMessage data)
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
                i.Instance.OnGroupMessageMentioned(groupId, chain, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }

        }
    }
    private void OnGroupMessageNotMentioned(long groupId, ReadOnlySpan<TypedMessage> chain, ReceivedGroupMessage data)
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
                i.Instance.OnGroupMessageNotMentioned(groupId, chain, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }
        }
    }
    private void OnGroupMessage(long groupId, ReadOnlySpan<TypedMessage> chain, ReceivedGroupMessage data)
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
                i.Instance.OnGroupMessage(groupId, chain, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }
        }
    }
}