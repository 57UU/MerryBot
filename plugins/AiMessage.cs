using Helpers;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using ZhipuClient;

namespace BotPlugin;

[PluginTag("AI机器人", "键入 #新对话 来开启新对话")]
public class AiMessage : Plugin
{
    RateLimiter rateLimiter = new RateLimiter(limitCount:3,limitTime:20);
    public AiMessage(PluginInterop interop) : base(interop)
    {
        
        Logger.Info("ai plugin start");
        var token = interop.GetVariable("ai-token", "");
        var prompt= interop.GetVariable("ai-prompt", "你是乐于助人的助手");
        zhipu = new ZhipuAi(token, prompt);
        zhipu.Logger = Logger;
        //add voice tool
        var voiceSender = new ToolDef();
        voiceSender.Function.Name = "send_voice";
        voiceSender.Function.Description = "发送语音（多用用，这样能展示你的个性）";
        voiceSender.Function.Parameters.Properties.Add("text", new ParameterProperty() { Type = "string", Description = "要发送成语言的内容" });
        voiceSender.Function.FunctionCall = async (parameters) =>
        {
            
            try
            {
                rateLimiter.Increase(parameters.SpecialTag);
                if (rateLimiter.CheckIsLimited(parameters.SpecialTag))
                {
                    throw new Exception("请求速率过高，请不要再发了");
                }
                string text = parameters["text"].GetString();
                await Actions.SendGroupAiVoice(parameters.SpecialTag.ToString(), text);
            }catch(Exception e)
            {
                return $"发送失败:{e.Message}";
            }
            return "发送成功。用户能看到你发的语音，你不必再去回复‘已发送’类似的话。";
        };
        zhipu.RegisterTool(voiceSender);
    }
    internal ZhipuAi zhipu;
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
    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        long selfId = BotUtils.GetSelfId(data);
        string nickname = data.sender.nickname;
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
            var messageId = data.message_id;
            handleMessage(groupId, text, messageId,nickname);
        }
    }
    async Task handleMessage(long groupId,string message,long messageId,string sender)
    {
        await foreach(var result in  zhipu.Ask(message, groupId, sender,groupId))
        {
            if (result != null)
            {
                await Actions.ChooseBestReplyMethod(groupId, messageId, result);
            }
        }
    }
    
}

