using BotPlugin;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Text;
using MessageChain = System.ReadOnlySpan<NapcatClient.Message>;

namespace MerryBot;

[PluginTag("MainPlugin", "特权插件，用于管理bot",priority:1919810)]
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
            var group = Config.Instance.QqGroups;
            if (group.Contains(groupId))
            {
                _= Actions.ReplyGroupMessage(groupId, data.message_id, "error: already active");
                return;
            }
            group.Add(groupId);
            Task.Run(async () => {
                await Config.save();
                await Actions.ReplyGroupMessage(groupId, data.message_id,$"active on {groupId}");
            });
        }
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/deactivate"))
        {
            if (!VerifyAuthority(groupId, data))
            {
                return;
            }
            Logger.Info($"execute deactivating on {groupId}");
            var result = Config.Instance.QqGroups.Remove(groupId);
            Task.Run(async () => {
                await Config.save();
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
    }
}
