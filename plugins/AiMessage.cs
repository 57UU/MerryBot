using Helpers;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using ZhipuClient;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace BotPlugin;

[PluginTag("AI机器人", "键入 #新对话 来开启新对话",isIgnore:false)]
public class AiMessage : Plugin
{
    RateLimiter rateLimiter = new RateLimiter(limitCount:3,limitTime:20);
    public AiMessage(PluginInterop interop) : base(interop)
    {
        
        Logger.Info("ai plugin start");
        var token = interop.GetVariable<string>("ai-token");
        if (token == null)
        {
            throw new Exception("请在配置文件variable中设置ai-token");
        }
        var prompt= interop.GetVariable("ai-prompt", "你是乐于助人的助手");
        zhipu = new ZhipuAi(token, prompt);
        zhipu.Logger = Logger;
        //add voice tool
        var voiceSender = new ToolDef();
        voiceSender.Function.Name = "send_voice";
        voiceSender.Function.Description = "发送语音/呼喊/唱歌";
        voiceSender.Function.Parameters.AddRequired("text", new ParameterProperty() { Type = "string", Description = "要发送成语言的内容" });
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
            return "发送成功。用户能看到你发的语音，你不必回复‘已发送’,也不必重复发送的信息";
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
        List<NapcatClient.Message> messages = new();
        foreach (var item in chain)
        {
            if (item.MessageType == "at")
            {
                string target = item.Data["qq"];
                if (target == selfId.ToString())
                {
                    isTargeted = true;
                }
                else
                {
                    messages.Add(item);
                }
            }
            else
            {
                messages.Add(item);
            }

        }
        if (!isTargeted)
        {
            return;
        }
        PreprocessMessage(messages,groupId,nickname,data.message_id);
    }
    async Task<string> extractMessage(IEnumerable<NapcatClient.Message> chain, long groupId, bool recursive=false)
    {
        StringBuilder sb = new();
        string? referenceMessage=null;
        foreach (var item in chain)
        {
            if (item.MessageType == "text")
            {
                sb.Append(item.Data["text"].Trim());
            } else if (item.MessageType == "at")
            {
                string qq = item.Data["qq"].ToString();
                var detail = await Actions.GetGroupMemberData(groupId.ToString(), qq);
                sb.Append($" @{detail.Nickname} ");
            }else if (item.MessageType == "reply" && recursive)
            {
                string referMessageId = item.Data["id"];
                var referMessage=await Actions.GetMessageById(referMessageId);
                var extractedMessage = await extractMessage(referMessage.Message, groupId, false);
                referenceMessage=$"\n引用内容：\n{extractedMessage}";
            }else if (item.MessageType == "json")
            {
                JsonElement json = JsonSerializer.Deserialize<JsonElement>(item.Data["data"]);
                if(json.TryGetProperty("meta",out var meta))
                {
                    if(meta.TryGetProperty("news",out var news))
                    {
                        sb.AppendLine(news.ToString());
                    }
                    else
                    {
                        sb.AppendLine(meta.ToString());
                    }
                }
                else
                {
                    sb.AppendLine(item.Data["data"]);
                }
            }
        }
        if (referenceMessage != null) { 
            sb.AppendLine(referenceMessage);
        }
        var text = sb.ToString();
        return text;
    }
    async Task PreprocessMessage(IEnumerable<NapcatClient.Message> chain,long groupId,string nickname,long messageId)
    {
        //concat text

        var text = await extractMessage(chain,groupId,true);
        if (text.StartsWith("/"))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {

            if (isContainsNew(text))
            {
                text = text.Replace("#新对话", "");
                Logger.Info("[New] " + text);
                zhipu.Reset(groupId);
            }
            handleMessage(groupId, text, messageId, nickname);
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

