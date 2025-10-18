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
        var model = ModelPreset.DeepSeekChat;
        Logger.Info($"ai plugin start. use model {model.model} by {model.provider}");
        var token_key= model.ApiTokenDictKey;
        var token = interop.GetVariable<string>(token_key);
        if (token == null)
        {
            throw new Exception($"请在配置文件variable中设置{token_key}");
        }
        var prompt= interop.GetVariable("ai-prompt", "你是乐于助人的助手");
        aiClient = new ZhipuAi(token, prompt, model);
        aiClient.Logger = Logger;
        //add voice tool
        var voiceSender = new ToolDef();
        voiceSender.Function.Name = "send_voice";
        voiceSender.Function.Description = "发送语音/唱歌";
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
            return "发送成功。你不必回复‘已发送’,也不必重复发送的信息";
        };
        aiClient.RegisterTool(voiceSender);
        // turn to another bot
        AddBotForHelp();

    }
    private void AddBotForHelp()
    {
        try
        {
            var anotherBot = Interop.GetJsonElement("bot-help");
            if (anotherBot == null)
            {
                throw new Exception("please specific bot-help in variables");
            }
            long qq = anotherBot.Value.GetInt64();
            var solver = new ToolDef();
            solver.Function.Name = "turn_to";
            solver.Function.Description = "让智慧AI处理某问题";
            solver.Function.Parameters.AddRequired("question", new ParameterProperty() { Type = "string", Description = "要处理的问题" });
            solver.Function.FunctionCall = async (parameters) =>
            {
                //verify bot in group
                var groupList = await Actions.GetGroupMemberData(parameters.SpecialTag.ToString(),qq.ToString());
                if (groupList == null)
                {
                    return "该工具无法使用，请不要再使用本工具";
                }
                var chain = Actions.EmptyMessageChain;
                chain.Add(NapcatClient.Message.At(qq.ToString()));
                chain.Add(NapcatClient.Message.Text($" {parameters["question"].ToString()}"));
                await Actions.SendGroupMessage(parameters.SpecialTag, chain);
                return "求助成功，你不用解决这个问题了";
            };
            solver.dynamicPrompt = "你比较疲惫，解决复杂问题可以直接转交给智慧AI（如果有的话）不用询问用户意见";
            solver.isUseable = async (tag) =>
            {
                var groupList = await Actions.GetGroupMemberData(tag.ToString(), qq.ToString());
                if (groupList == null)
                {
                    return false;
                }
                return true;
            };
            aiClient.RegisterTool(solver);
            //拦截bot对自己发送的消息
            Interop.Interceptors.Add((data) => {
                return data.sender.user_id==qq;
            });

        }
        catch (Exception e)
        {
            Logger.Warn($"load bot help failed:{e.Message}");
        }
    }
    public override void OnLoaded()
    {
        var shellPlugin = Interop.FindPlugin<RunCommand>();
        if (shellPlugin != null)
        {
            //add linux shell
            var shell = new ToolDef();
            shell.Function.Name = "shell";
            shell.Function.Description = "执行linux bash shell命令.仅支持使用';'连接多条指令";
            shell.Function.Parameters.AddRequired("command", new ParameterProperty() { Type = "string", Description = "要执行的命令" });
            shell.Function.FunctionCall = async (parameters) => {
                return await shellPlugin.terminal.RunCommandAutoTimeoutAsync(parameters["command"].GetString());
            };
            aiClient.RegisterTool(shell);
        }
        else
        {
            Logger.Warn("shell plugin not found, ai can not use it");
        }

    }
    internal IAiClient aiClient;
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
                        try
                        {
                            sb.AppendLine(
                                $"描述:{news.GetProperty("desc").ToString()}\n" +
                                $"URL:'{news.GetProperty("jumpUrl").ToString()}'"
                                );
                        }
                        catch (Exception) {
                            sb.AppendLine(news.ToString());
                        }
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
            }else if(item.MessageType== "forward")
            {
                //转发消息
                string msgId= item.Data["id"];
                var referMessage=await Actions.GetForwardMessageById(msgId);
                if (referMessage != null)
                {
                    StringBuilder forwardString = new();
                    forwardString.AppendLine("---转发消息---");
                    foreach (var msg in referMessage.Messages)
                    {
                        var extractedMessage = await extractMessage(msg.Message, groupId, false);
                        forwardString.AppendLine($"{msg.SenderInfo.nickname}:{extractedMessage}");
                    }
                    forwardString.AppendLine("------");
                    sb.AppendLine(
                        PluginUtils.ConstraintLength(forwardString.ToString(), 600)
                        );
                }
                else
                {
                    sb.AppendLine("<转发消息>");
                }

            
            }else if(item.MessageType == "image")
            {
                sb.AppendLine("<image>");
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
        string text;
        try
        {
            text = await extractMessage(chain, groupId, true);
        }
        catch (Exception ex) {
            Logger.Error($"extract failed:{ex.Message}\n{ex.StackTrace}");
            return;
        }
        
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
                aiClient.Reset(groupId);
            }
            handleMessage(groupId, text, messageId, nickname);
        }
    }
    async Task handleMessage(long groupId,string message,long messageId,string sender)
    {
        await foreach(var result in  aiClient.Ask(message, groupId, sender,groupId))
        {
            if (result != null)
            {
                await Actions.ChooseBestReplyMethod(groupId, messageId, result);
            }
        }
    }
    
}

