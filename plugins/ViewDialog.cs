using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZhipuClient;

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
    const int lengthConstraint = 25;
    static string ConstraintLength(string s)
    {
        if (s.Length > lengthConstraint)
        {
            return s.Substring(0, lengthConstraint) + "...";
        }
        return s;
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
                    sb.Append("• ");
                    if (item.Role == ZhipuAi.SYSTEM)
                    {
                        sb.AppendLine("system: <HIDDEN>");
                    }
                    else if (item.Role == ZhipuAi.ASSISTANT) { 
                        var item2 = item as AssistantMessage;
                        sb.Append("assistant: " + ConstraintLength(item.Content.Trim()));
                        if(item2?.ToolCalls!=null && item2.ToolCalls.Count>0)
                        {
                            foreach(var i in item2.ToolCalls)
                            {
                                sb.Append($"[TOOL:{i.Function.Name} {ConstraintLength(i.Function.Arguments)}]");
                            }
                        }
                        sb.AppendLine();
                        
                    }
                    else
                    {
                        sb.AppendLine(item.Role + ": " + ConstraintLength(item.Content));
                    }
                }
                Actions.ChooseBestReplyMethod(groupId, data.message_id, sb.ToString().Trim());
            }

        }
    }
}
