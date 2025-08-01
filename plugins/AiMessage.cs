using Helpers;
using NapcatClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ZhipuClient;

namespace BotPlugin;

[PluginTag("AI机器人", "键入 #新对话 来开启新对话")]
public class AiMessage : Plugin
{
    public AiMessage(PluginInterop interop) : base(interop)
    {
        Logger.Info("ai plugin start");
        var token = interop.GetVariable("ai-token", "");
        var prompt= interop.GetVariable("ai-prompt", "你是乐于助人的助手");
        zhipu = new ZhipuAi(token, prompt);
    }
    ZhipuAi zhipu;
    bool isContainsNew(string message)
    {
        var l=message.Split(" ");
        foreach(var item in l)
        {
            if (item == "#新对话")
            {
                return true;
            }
        }
        return false;
    }
    public override void OnGroupMessage(long groupId, MessageChain chain, Dictionary<string, dynamic> data)
    {
        long selfId = BotUtils.GetSelfId(data);
        string nickname = data["sender"]["nickname"];
        bool isTargeted = false;
        foreach (var item in chain)
        {
            if (item.MessageType == "at")
            {
                string target = item.Data["qq"];
                if (target == selfId.ToString())
                {
                    isTargeted = true;
                }
            }

        }
        if (!isTargeted)
        {
            return;
        }
        //find first text
        bool find=false;
        string text="";
        foreach(var item in chain)
        {
            if (item.MessageType == "text")
            { 
                text = item.Data["text"];
                find=true;
            }
        }
        text = text.Trim();
        if (text.StartsWith("/"))
        {
            return;
        }
        if (find)
        {
            
            if (isContainsNew(text))
            {
                text=text.Replace("#新对话","");
                Logger.Info("[New] " + text);
                zhipu.Reset(groupId);
            }
            var messageId = data["message_id"];
            handleMessage(groupId, text, messageId,nickname);
        }
    }
    async Task handleMessage(long groupId,string message,long messageId,string sender)
    {
        await foreach(var result in  zhipu.Ask(message, groupId, sender))
        {
            if (result != null)
            {
                await Actions.ReplyGroupMessage(groupId, messageId, result);
            }
        }
    }
    
}

