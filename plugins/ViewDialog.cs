using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("AiDialog", "使用 /dialog 来查看AI历史")]
public class ViewDialog : Plugin
{
    public ViewDialog(PluginInterop interop) : base(interop)
    {
        Logger.Info("ViewDialog plugin start");
    }
    AiMessage aiMessage;
    public override void OnLoaded()
    {
        aiMessage=Interop.FindPlugin<AiMessage>()!;
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/dialog"))
        {
            var history=aiMessage.zhipu.GetDialodHistory(groupId);
            if (history.Length == 0)
            {
                Actions.SendGroupMessage(groupId, "<EMPTY>");
            }
            else
            {
                StringBuilder sb = new();
                foreach (var item in history)
                {
                    if(item.Role == "system")
                    {
                        sb.AppendLine("system: <HIDDEN>");
                    }
                    else
                    {
                        sb.AppendLine(item.Role + ": " + item.Content);
                    }
                }
                Actions.SendGroupMessage(groupId, sb.ToString());
            }

        }
    }
}
