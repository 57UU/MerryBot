using BrowserService;
using NapcatClient;
using NapcatClient.MessageType;
using OpenAiClient;

namespace BotPlugin;

[PluginTag("agent", "AI机器人", "键入 #新对话 来开启新对话;/setllm 设置模型;/getllm 查看模型", isIgnore: false)]
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
    internal ModelPreset defaultModel;

    /// <summary>
    /// Browser 实例，由 AiMessage 插件管理生命周期，可注入到其他需要的组件
    /// </summary>
    public readonly Browser browser;

    internal string? GetToken(ModelPreset modelPreset){
        var token_key = modelPreset.ApiTokenDictKey;
        var token = Interop.GetClassVariable<string>(token_key);
        return token;
    }
    public AiMessage(PluginInterop interop, StorageManagerPlugin storageManager, ExtraModels __) : base(interop)
    {
        this.storageManager = storageManager;
        this.aiMessageStorage = storageManager.AiMessageStorage;

        // 初始化 Browser 实例
        browser = new Browser(new BrowserOptions { BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN") });
        //display available model
        //ModelPreset.DisplayAllModels();
        var model = ModelPreset.GetModelByName(
            interop.GetClassVariable<string>(LLM_KEY)
            );
        if (model == null)
        {
            Logger.Warn("please specific 'llm-model' in setting/variables;rollback to GLM4.5 Free");
            model = ModelPreset.Glm_4_5_Free;
        }
        useFunctionCallToReply = interop.GetStructVariable<bool>("use_function_call_reply") ?? false;
        Logger.Info($"ai plugin start. use model {model.model} by {model.provider}");
        var token_key = model.ApiTokenDictKey;
        var token = GetToken(model)
            ?? throw new PluginNotUsableException($"请在配置文件variable中设置{token_key}");
        //image interpreter
        {
            var imageInterpreters = new List<ImageInterpreter>();
            foreach (var model_image in imageInterpreterModels)
            {
                var token_key_image = model_image.ApiTokenDictKey;
                var token_image = interop.GetClassVariable<string>(token_key_image);
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

        aiClient = new OpenAiCompatible(token, prompt, model, historyRecorder, browser: browser);
        defaultModel = model;
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
        RegisterShellTool();
        RegisterMarkdownSender();
        RegisterContextTool();
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
                string? token = Interop.GetClassVariable<string>(model.ApiTokenDictKey);
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
        browser.Dispose();
        ImageInterpreterPool?.Dispose();

        GC.SuppressFinalize(this);
    }

}
