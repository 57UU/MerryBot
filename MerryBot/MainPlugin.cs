using BotPlugin;
using NapcatClient;
using MessageChain = System.ReadOnlySpan<NapcatClient.MessageType.TypedMessage>;

namespace MerryBot;

[PluginTag("main-plugin", "MainPlugin", "特权插件，用于管理bot", priority: 1919810, type: PluginType.Admin)]
internal class MainPlugin : Plugin
{
    private Logic logic;
    public MainPlugin(PluginInterop interop, Logic logic) : base(interop)
    {
        this.logic = logic;
    }
    bool VerifyAuthority(long groupId, ReceivedGroupMessage data)
    {
        if (data.sender.user_id != Interop.AuthorizedUser)
        {
            _ = Actions.ReplyGroupMessage(groupId, data.message_id, "Permission Denied: Unauthorized");
            return false;
        }
        return true;
    }
    public void OnMessageMentionedNotInGroup(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/activate"))
        {
            if (!VerifyAuthority(groupId, data))
            {
                return;
            }
            Logger.Info($"execute activating on {groupId}");
            var group = ConfigManager.Instance.QqGroups;
            if (group.Contains(groupId))
            {
                _ = Actions.ReplyGroupMessage(groupId, data.message_id, "error: already active");
                return;
            }
            group.Add(groupId);
            Task.Run(async () =>
            {
                await ConfigManager.Save();
                await Actions.ReplyGroupMessage(groupId, data.message_id, $"active on {groupId}");
            });
        }
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (!VerifyAuthority(groupId, data))
        {
            return;
        }
        if (IsStartsWith(chain, "/deactivate"))
        {
            Logger.Info($"execute deactivating on {groupId}");
            var result = ConfigManager.Instance.QqGroups.Remove(groupId);
            Task.Run(async () =>
            {
                await ConfigManager.Save();
                if (!result)
                {
                    await Actions.ReplyGroupMessage(groupId, data.message_id, "error: not active");
                }
                else
                {
                    await Actions.ReplyGroupMessage(groupId, data.message_id, $"inactive on {groupId}");
                }
            });
        }
        else if (IsStartsWith(chain, "/reload"))
        {
            Logger.Info($"execute reload");
            _ = Actions.ReplyGroupMessage(groupId, data.message_id, "Reloading...");
            Task.Run(() =>
            {
                logic.Reload();
            });
        }
    }
}
