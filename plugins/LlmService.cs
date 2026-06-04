using BrowserService;
using OpenAiClient;

namespace BotPlugin;

/// <summary>
/// LLM 服务后台插件，提供统一的模型解析、Token 获取和 Browser 管理
/// </summary>
[PluginTag("llm-service", "LlmService", "LLM服务，提供模型/token/browser", type: PluginType.Background)]
public class LlmService : Plugin
{
    /// <summary>
    /// 共享的 Browser 实例，由 LlmService 管理生命周期
    /// </summary>
    public readonly Browser Browser;

    /// <summary>
    /// 默认模型（来自 config "defaultllm"，默认 deepseek-chat）
    /// </summary>
    public ModelPreset DefaultModel { get; }

    /// <summary>
    /// 图片描述（vision）模型池。多个模型 fallback，token 缺失的模型会被跳过；全部缺失时为 null。
    /// </summary>
    public readonly ImageInterpreterPool? ImageInterpreterPool;

    private static readonly ModelPreset[] ImageInterpreterModels =
    [
        ModelPreset.GLM_4_6V_Flash_Free,
        ModelPreset.Glm_4_1V_Flash_Free,
        ModelPreset.Glm_4V_Flash_Free
    ];

    public LlmService(PluginInterop interop) : base(interop)
    {
        Browser = new Browser(new BrowserOptions
        {
            BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN"),
            ChromeDriverPath = interop.GetVariableOrSetDefault("chromedriver-path", "")
        });

        var defaultModelTag = interop.GetVariableOrSetDefault("default-llm", ModelPreset.DeepSeekChat.ModelTag);
        DefaultModel = ModelPreset.GetModelByName(defaultModelTag) ?? ModelPreset.DeepSeekChat;
        Logger.Info($"LlmService started, default model: {DefaultModel.ModelTag}");

        // 构造 image interpreter pool（vision 模型的 fallback 池）
        {
            var imageInterpreters = new List<ImageInterpreter>();
            foreach (var modelImage in ImageInterpreterModels)
            {
                var tokenImage = GetToken(modelImage);
                if (tokenImage == null)
                {
                    Logger.Warn($"请在配置文件variable中设置{modelImage.ApiTokenDictKey}");
                }
                else
                {
                    imageInterpreters.Add(new ImageInterpreter(modelImage, tokenImage));
                }
            }
            if (imageInterpreters.Count > 0)
            {
                ImageInterpreterPool = new ImageInterpreterPool(imageInterpreters);
            }
        }
    }

    /// <summary>
    /// 根据名称解析模型，空值或无效值回退到 defaultllm
    /// </summary>
    public ModelPreset ResolveModel(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var model = ModelPreset.GetModelByName(name);
            if (model != null) return model;
        }
        return DefaultModel;
    }

    /// <summary>
    /// 根据模型获取 API token（从插件 config 中读取 ai-token-{provider}）
    /// </summary>
    public string? GetToken(ModelPreset modelPreset)
    {
        return Interop.GetClassVariable<string>(modelPreset.ApiTokenDictKey);
    }

    public override void Dispose()
    {
        Browser.Dispose();
        ImageInterpreterPool?.Dispose();
        base.Dispose();
    }
}
