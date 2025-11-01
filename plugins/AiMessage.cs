using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    readonly RateLimiter rateLimiter = new RateLimiter(limitCount:3,limitTime:20);
    public AiMessage(PluginInterop interop) : base(interop)
    {
        //display available model
        ModelPreset.DisplayAllModels();
        var model = ModelPreset.GetModelByName(
            interop.GetVariable<string>("llm-model")
            );
        if (model == null)
        {
            Logger.Warn("please specific 'llm-model' in setting/variables;rollback to GLM4.5 Free");
            model = ModelPreset.Glm_4_5_Free;
        }
        Logger.Info($"ai plugin start. use model {model.model} by {model.provider}");
        var token_key= model.ApiTokenDictKey;
        var token = interop.GetVariable<string>(token_key) 
            ?? throw new PluginNotUsableException($"请在配置文件variable中设置{token_key}");
        var prompt = interop.GetVariable("ai-prompt", "你是乐于助人的助手");
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
                string text = parameters["text"].GetString()!;
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
            var anotherBot = Interop.GetJsonElement("bot-help") 
                ?? throw new Exception("please specific bot-help in variables");
            long qq = anotherBot.GetInt64();
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
                var chain = NapcatClient.Action.Actions.EmptyMessageChain;
                chain.Add(NapcatClient.Message.At(qq.ToString()));
                chain.Add(NapcatClient.Message.Text($" {parameters["question"]}"));
                await Actions.SendGroupMessage(parameters.SpecialTag, chain);
                return "求助成功，你不用解决这个问题了";
            };
            solver.DynamicPrompt = "如果问题非常复杂，请智慧AI求助";
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
    public async override Task OnLoaded()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var terminal = new Terminal();
            //add Linux shell
            var shell = new ToolDef();
            int timeout = 5;//second
            shell.Function.Name = "shell";
            shell.Function.Description = $"执行Linux bash shell命令.(限时{timeout}s)";
            shell.Function.Parameters.AddRequired("command", new ParameterProperty() { Type = "string", Description = "要执行的命令" });
            shell.Function.FunctionCall = async (parameters) => {
                var result= await terminal.RunCommandAsync(
                    parameters["command"].GetString()!,
                    timeoutMs: timeout*1000,
                    useHardTimeout:true
                    );
                return PluginUtils.ConstraintLength(result, 1500);
            };
            aiClient.RegisterTool(shell);
        }
        else
        {
            Logger.Warn("only Linux shell is supported, ai can not use it");
        }

    }
    internal ZhipuAi aiClient;
    static bool IsContainsNew(string message)
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
        _=PreprocessMessage(messages,groupId,nickname,data.message_id);
    }
    async Task<string> ExtractMessage(IEnumerable<NapcatClient.Message> chain, long groupId, bool recursive=false)
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
                if (detail != null) {
                    sb.Append($" @{detail.Nickname} ");
                }
                else
                {
                    sb.Append($" @unknown ");
                }
                
            }else if (item.MessageType == "reply" && recursive)
            {
                string referMessageId = item.Data["id"];
                var referMessage=await Actions.GetMessageById(referMessageId);
                if (referMessage != null)
                {
                    var extractedMessage = await ExtractMessage(referMessage.Message, groupId, false);
                    referenceMessage = $"\n引用内容：\n{extractedMessage}";
                }
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
                        var extractedMessage = await ExtractMessage(msg.Message, groupId, false);
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
            text = await ExtractMessage(chain, groupId, true);
        }
        catch (Exception ex) {
            Logger.Error($"extract failed:{ex.Message}\n{ex.StackTrace}");
            return;
        }
        
        if (text.StartsWith('/'))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {

            if (IsContainsNew(text))
            {
                text = text.Replace("#新对话", "");
                Logger.Info("[New] " + text);
                aiClient.Reset(groupId);
            }
            _ = HandleMessage(groupId, text, messageId, nickname);
        }
    }
    async Task HandleMessage(long groupId,string message,long messageId,string sender)
    {
        await foreach(var result in  aiClient.Ask(message, groupId, sender,groupId))
        {
            if (!string.IsNullOrWhiteSpace(result))
            {
                await Actions.ChooseBestReplyMethod(groupId, messageId, result);
            }
        }
    }
    public override void Dispose()
    {
        aiClient.Dispose();
        GC.SuppressFinalize(this);
    }
    
}

