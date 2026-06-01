using NapcatClient;
using NapcatClient.MessageType;
using OpenAiClient;

namespace BotPlugin;

[PluginTag("agent", "AI机器人", "键入 #新对话 或 /new 来开启新对话;/setllm 设置模型;/getllm 查看模型", isIgnore: false)]
public partial class AiMessage : Plugin
{
    bool useFunctionCallToReply;
    readonly RateLimiter rateLimiter = new RateLimiter(limitCount: 3, limitTime: 20);
    readonly RateLimiter messageRateLimiter = new RateLimiter(limitCount: 3, limitTime: 8);
    const string LLM_KEY = "llm-model";
    private DataService.HistoryRecorder aiMessageStorage;
    private readonly ImageInterpreterPool? ImageInterpreterPool;
    private readonly StorageManagerPlugin storageManager;
    private readonly ModelPreset[] imageInterpreterModels = [
        ModelPreset.GLM_4_6V_Flash_Free,
        ModelPreset.Glm_4_1V_Flash_Free,
        ModelPreset.Glm_4V_Flash_Free
        ];
    private readonly LlmService llmService;
    private readonly RunCommand? runCommand;
    public AiMessage(PluginInterop interop, StorageManagerPlugin storageManager, LlmService llmService, ExtraModels __, RunCommand? runCommand = null) : base(interop)
    {
        this.storageManager = storageManager;
        this.llmService = llmService;
        this.runCommand = runCommand;
        this.aiMessageStorage = storageManager.AiMessageStorage;

        var model = llmService.ResolveModel(interop.GetClassVariable<string>(LLM_KEY));
        useFunctionCallToReply = interop.GetStructVariable<bool>("use_function_call_reply") ?? false;
        Logger.Info($"ai plugin start. use model {model.model} by {model.provider}");
        var token = llmService.GetToken(model)
            ?? throw new PluginNotUsableException($"请在配置文件 LlmService 中设置{model.ApiTokenDictKey}");
        //image interpreter
        {
            var imageInterpreters = new List<ImageInterpreter>();
            foreach (var model_image in imageInterpreterModels)
            {
                var token_key_image = model_image.ApiTokenDictKey;
                var token_image = llmService.GetToken(model_image);
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
        var prompt = interop.GetVariableOrSetDefault("ai-prompt", "你是乐于助人的助手");

        OpenAiClient.HistoryRecorder historyRecorder = (groupId, messageType, content) =>
        {
            _ = aiMessageStorage.RecordAiMessageAsync(groupId, messageType, content);
        };

        aiClient = new OpenAiCompatible(token, prompt, model, historyRecorder, browser: llmService.Browser);
        aiClient.Logger = Logger;

        // webview summarizer
        var summarizerModelName = interop.GetVariableOrSetDefault<string>("webview-summarizer-model",string.Empty);
        if (!string.IsNullOrEmpty(summarizerModelName))
        {
            var summarizerModel = ModelPreset.GetModelByName(summarizerModelName);
            if (summarizerModel != null)
            {
                var summarizerToken = llmService.GetToken(summarizerModel);
                if (summarizerToken != null)
                {
                    aiClient.WebviewSummarizer = new WebviewSummarizer(summarizerToken, summarizerModel);
                    Logger.Info($"webview summarizer enabled: {summarizerModel.model}");
                }
                else
                {
                    Logger.Warn($"请在配置文件variable中设置{summarizerModel.ApiTokenDictKey}");
                }
            }
            else
            {
                Logger.Warn($"无效的 webview-summarizer-model: {summarizerModelName}");
            }
        }

        // auto-compress context management
        aiClient.AutoCompressEnabled = interop.GetStructVariable<bool>("auto-compress-enabled") ?? true;
        var compressThreshold = interop.GetStructVariable<int>("compress-token-threshold");
        if (compressThreshold.HasValue)
            aiClient.CompressTokenThreshold = compressThreshold.Value;
        var compressModelName = interop.GetVariableOrSetDefault<string>("compress-model", string.Empty);
        if (!string.IsNullOrEmpty(compressModelName))
        {
            var compressModel = ModelPreset.GetModelByName(compressModelName);
            if (compressModel != null)
            {
                var compressToken = llmService.GetToken(compressModel);
                if (compressToken != null)
                {
                    aiClient.CompressionModel = compressModel;
                    aiClient.CompressionToken = compressToken;
                    Logger.Info($"compression model enabled: {compressModel.model}");
                }
                else
                {
                    Logger.Warn($"请在配置文件variable中设置{compressModel.ApiTokenDictKey}");
                }
            }
            else
            {
                Logger.Warn($"无效的 compress-model: {compressModelName}");
            }
        }

        //tools
        RegisterVoiceTool();
        if (useFunctionCallToReply)
        {
            RegisterReplyTool();
        }
        // turn to another bot
        //AddBotForHelp();
        if (interop.GetStructVariable<bool>("enable_image_painter") ?? false)
        {
            RegisterImagePainter();
        }
        RegisterShellTool();
        RegisterMarkdownSender();
        RegisterContextTool();
        RegisterMemoryTools();
    }


    private ImagePainterDashscope? imagePainter;

    public async override Task OnLoaded()
    {

    }
    internal OpenAiCompatible aiClient;
    static bool IsContainsNew(string message)
    {
        var l = message.Split(" ");
        foreach (var item in l)
        {
            if (item == "#新对话" || item == "/new")
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
                string? token = llmService.GetToken(model);
                if (token != null)
                {
                    //valid
                    aiClient.SetModelPreset(model, token);
                    Interop.SetVariable(LLM_KEY, tag);
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
                _ = Actions.ReplyGroupMessage(groupId, messageId, $"current llm: {aiClient.ModelPreset.model}\n{string.Join(", ", ModelPreset.AllModelsDict.Keys)}");
            }
            else if (text.StartsWith("/new"))
            {
                text = text.Replace("/new", "").Trim();
                Logger.Info("[New] " + text);
                aiClient.Reset(groupId);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _ = HandleMessage(groupId, text, messageId, nickname, data.sender.user_id);
                }
            }
            else if (text.StartsWith("/stop"))
            {
                if (aiClient.CancelRequest(groupId))
                {
                    _ = Actions.ReplyGroupMessage(groupId, messageId, "已停止当前请求");
                }
                else
                {
                    _ = Actions.ReplyGroupMessage(groupId, messageId, "当前没有进行中的请求");
                }
            }
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {

            if (IsContainsNew(text))
            {
                text = text.Replace("#新对话", "").Replace("/new", "").Trim();
                Logger.Info("[New] " + text);
                aiClient.Reset(groupId);
            }
            _ = HandleMessage(groupId, text, messageId, nickname, data.sender.user_id);
        }
    }
    async Task HandleMessage(long groupId, string message, long messageId, string sender, long senderQq)
    {
        try{
            await foreach (var result in aiClient.Ask(message, groupId, $"[user:{sender}]", groupId))
        {
            if (!useFunctionCallToReply && !string.IsNullOrWhiteSpace(result))
            {
                await Actions.ChooseBestReplyMethod(groupId, senderQq.ToString(), result);
            }
        }
        }
        catch (NotAvailableException)
        {
            await Actions.SendGroupMessage(groupId,$"请等待上一个请求完成哦");
        }
    }
    public override void Dispose()
    {
        aiClient.Dispose();
        ImageInterpreterPool?.Dispose();

        GC.SuppressFinalize(this);
    }

}
