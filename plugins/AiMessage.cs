using NapcatClient;
using NapcatClient.MessageType;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using ZhipuClient;
using DataService;

namespace BotPlugin;

[PluginTag("AI机器人", "键入 #新对话 来开启新对话;/setllm 设置模型;/getllm 查看模型", isIgnore: false)]
public partial class AiMessage : Plugin
{
    bool useFunctionCallToReply;
    readonly RateLimiter rateLimiter = new RateLimiter(limitCount: 3, limitTime: 20);
    readonly RateLimiter messageRateLimiter = new RateLimiter(limitCount: 3, limitTime: 8);
    const string LLM_KEY = "llm-model";
    private DataService.HistoryRecorder aiMessageStorage;
    private readonly ImageInterpreterPool? ImageInterpreterPool;
    private readonly ModelPreset[] imageInterpreterModels = [
        ModelPreset.GLM_4_6V_Flash_Free,
        ModelPreset.Glm_4_1V_Flash_Free,
        ModelPreset.Glm_4V_Flash_Free
        ];
    public AiMessage(PluginInterop interop, StorageManagerPlugin storageManager) : base(interop)
    {
        this.aiMessageStorage = storageManager.AiMessageStorage;
        //display available model
        ModelPreset.DisplayAllModels();
        var model = ModelPreset.GetModelByName(
            interop.GetVariable<string>(LLM_KEY)
            );
        if (model == null)
        {
            Logger.Warn("please specific 'llm-model' in setting/variables;rollback to GLM4.5 Free");
            model = ModelPreset.Glm_4_5_Free;
        }
        useFunctionCallToReply = interop.GetJsonElement("use_function_call_reply")?.GetBoolean() ?? false;
        Logger.Info($"ai plugin start. use model {model.model} by {model.provider}");
        var token_key = model.ApiTokenDictKey;
        var token = interop.GetVariable<string>(token_key)
            ?? throw new PluginNotUsableException($"请在配置文件variable中设置{token_key}");
        //image interpreter
        {
            var imageInterpreters = new List<ImageInterpreter>();
            foreach (var model_image in imageInterpreterModels)
            {
                var token_key_image = model_image.ApiTokenDictKey;
                var token_image = interop.GetVariable<string>(token_key_image);
                if (token_image == null)
                {
                    Logger.Warn($"请在配置文件variable中设置{token_key_image}");
                }
                else
                {
                    imageInterpreters.Add(new ImageInterpreter(model_image, token_image));
                }
            }
            if (imageInterpreters.Count > 0)
            {
                ImageInterpreterPool = new ImageInterpreterPool(imageInterpreters);
            }
        }
        var prompt = interop.GetVariable("ai-prompt", "你是乐于助人的助手");

        ZhipuClient.HistoryRecorder historyRecorder = (groupId, messageType, content) =>
        {
            _ = aiMessageStorage.RecordAiMessageAsync(groupId, messageType, content);
        };

        aiClient = new ZhipuAi(token, prompt, model, historyRecorder);
        aiClient.Logger = Logger;
        //tools
        RegisterVoiceTool();
        if (useFunctionCallToReply)
        {
            RegisterReplyTool();
        }
        // turn to another bot
        //AddBotForHelp();
        RegisterImagePainter();
        RegisterFileSenderTool();
    }


    private ImagePainterDashscope? imagePainter;

    public async override Task OnLoaded()
    {
        RegisterShellTool();
    }
    internal ZhipuAi aiClient;
    static bool IsContainsNew(string message)
    {
        var l = message.Split(" ");
        foreach (var item in l)
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
        List<TypedMessage> messages = new();
        foreach (var item in chain)
        {
            if (item is AtData atData)
            {
                string target = atData.Qq;
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
        if (isTargeted)
        {
            _ = PreprocessMessage(messages, groupId, nickname, data);
        }
    }
    async Task SetLlmModel(string text, long groupId, long messageId)
    {
        string[] textList = text.Split(' ');
        if (textList.Length > 1)
        {
            var tag = textList[1];
            var model = ModelPreset.GetModelByName(tag);
            if (model != null)
            {
                //access token
                string? token = Interop.GetVariable<string>(model.ApiTokenDictKey);
                if (token != null)
                {
                    //valid
                    aiClient.SetModelPreset(model, token);
                    Interop.SetVarible(LLM_KEY, tag);
                    await Interop.SaveConfig();
                    _ = Actions.ReplyGroupMessage(groupId, messageId, $"set model: {tag}");
                }
                else
                {
                    _ = Actions.ReplyGroupMessage(groupId, messageId, $"no token for: {model.ApiTokenDictKey}");
                }
            }
            else
            {
                _ = Actions.ReplyGroupMessage(groupId, messageId, $"invalid model tag\n{string.Join(",", ModelPreset.AllModelsDict.Keys)}");
            }
        }
        else
        {
            _ = Actions.ReplyGroupMessage(groupId, messageId, $"/setllm [model-tag]\n{string.Join(",", ModelPreset.AllModelsDict.Keys)}");
        }

    }
    async Task<string> ExtractMessage(IEnumerable<TypedMessage> chain, long groupId, bool recursive = false, int depth = 0, bool interpretImage = false)
    {
        StringBuilder sb = new();
        string? referenceMessage = null;
        foreach (var item in chain)
        {
            if (item is TextData textData)
            {
                sb.Append(textData.Text.Trim());
            }
            else if (item is AtData atData)
            {
                string qq = atData.Qq;
                var detail = await Actions.GetGroupMemberData(groupId.ToString(), qq);
                if (detail != null)
                {
                    sb.Append($" @{detail.Nickname} ");
                }
                else
                {
                    sb.Append($" @unknown ");
                }

            }
            else if (item is ReplyData replyData && recursive)
            {
                string referMessageId = replyData.Id;
                var referMessage = await Actions.GetMessageById(referMessageId);
                if (referMessage != null)
                {
                    var extractedMessage = await ExtractMessage(referMessage.Message, groupId, false, depth + 1, interpretImage: true);
                    referenceMessage = $"\n引用内容：\n{extractedMessage}";
                }
            }
            else if (item is JsonData jsonData)
            {
                JsonElement json = jsonData.Data;
                if (json.TryGetProperty("meta", out var meta))
                {
                    if (meta.TryGetProperty("news", out var news))
                    {
                        try
                        {
                            sb.AppendLine(
                                $"描述:{news.GetProperty("desc").ToString()}\n" +
                                $"URL:'{news.GetProperty("jumpUrl").ToString()}'"
                                );
                        }
                        catch (Exception)
                        {
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
                    sb.AppendLine(json.ToString());
                }
            }
            else if (item is ForwardData forwardData)
            {
                //转发消息
                string msgId = forwardData.Id;
                var referMessage = await Actions.GetForwardMessageById(msgId);
                if (referMessage != null)
                {
                    StringBuilder forwardString = new();
                    forwardString.AppendLine("---转发消息---");
                    foreach (var msg in referMessage.Messages)
                    {
                        var extractedMessage = await ExtractMessage(msg.Message, groupId, depth < 3, depth + 1);
                        forwardString.AppendLine($"{msg.SenderInfo.nickname}:{extractedMessage}");
                    }
                    forwardString.AppendLine("------");
                    sb.AppendLine(
                        PluginUtils.ConstraintLength(forwardString.ToString(), 600)
                        );
                }
                else
                {
                    sb.AppendLine("<转发消息/>");
                }


            }
            else if (item is ImageData imageData)
            {
                if ((depth == 0 || interpretImage) && ImageInterpreterPool != null && imageData.Url != null)
                {
                    //解析图片内容
                    var imageUrl = imageData.Url;
                    try
                    {
                        var description = await ImageInterpreterPool!.Interpret(imageUrl);
                        sb.AppendLine($"<image：{description}/>");
                    }
                    catch (Exception)
                    {
                        sb.AppendLine($"<image/>");
                    }
                }
                else
                {
                    sb.AppendLine("<image/>");
                }

            }
            else if (item is FaceData faceData)
            {
                if (int.TryParse(faceData.Id, out int faceCode))
                {
                    string faceName = QqFace.GetFace(faceCode);
                    sb.AppendLine($" <表情:{faceName}/>");
                }
            }
        }
        if (referenceMessage != null)
        {
            sb.AppendLine(referenceMessage);
        }
        var text = sb.ToString();
        return text;
    }
    async Task PreprocessMessage(IEnumerable<TypedMessage> chain, long groupId, string nickname, ReceivedGroupMessage data)
    {
        var messageId = data.message_id;
        //concat text
        string text;
        try
        {
            text = await ExtractMessage(chain, groupId, true);
        }
        catch (Exception ex)
        {
            Logger.Error($"extract failed:{ex.Message}\n{ex.StackTrace}");
            return;
        }

        if (text.StartsWith('/'))
        {
            if (text.StartsWith("/setllm"))
            {
                _ = SetLlmModel(text, groupId, messageId);
            }
            else if (text.StartsWith("/getllm"))
            {
                _ = Actions.ReplyGroupMessage(groupId, messageId, $"current llm: {aiClient.ModelPreset.model}\n{ModelPreset.AllModels()}");
            }
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
    async Task HandleMessage(long groupId, string message, long messageId, string sender)
    {
        await foreach (var result in aiClient.Ask(message, groupId, sender, groupId))
        {
            if (!useFunctionCallToReply && !string.IsNullOrWhiteSpace(result))
            {
                await Actions.ChooseBestReplyMethod(groupId, messageId, result);
            }
        }
    }
    public override void Dispose()
    {
        aiClient.Dispose();

        // AiMessageStorage 由 StorageManagerPlugin 负责释放

        GC.SuppressFinalize(this);
    }

}
