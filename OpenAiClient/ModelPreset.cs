using System.Collections.Immutable;
using System.Text;

namespace OpenAiClient;

public class ModelPreset
{
    public readonly string model;
    /// <summary>
    /// 是否启用内置的 搜索 工具
    /// </summary>
    public readonly bool enableSearch;
    public readonly string url;
    public readonly string provider;
    public readonly bool supportImageInput;
    public readonly float? temperature;
    public readonly ImmutableDictionary<string, object> extraBody;
    private static ImmutableDictionary<string, object> Empty = ImmutableDictionary<string, object>.Empty;

    private static readonly Dictionary<string, ModelPreset> modelsByName = new();
    /// <summary>
    /// OpenAI api format
    /// </summary>
    public string CompletionUrl => $"{url}/chat/completions";
    public ModelPreset(
        string model,
        string url,
        string provider,
        ImmutableDictionary<string, object>? extraBody = null,
        bool enableSearch = true,
        bool supportImageInput = false,
        float? temperature = null,
        bool? storeInDict = true
        )
    {
        this.model = model;
        this.url = url;
        this.provider = provider;
        this.extraBody = extraBody ?? Empty;
        this.enableSearch = enableSearch;
        this.supportImageInput = supportImageInput;
        this.temperature = temperature;
        if (storeInDict == true)
        {
            modelsByName[ModelTag] = this;
        }
    }
    public string ModelTag=>$"{provider}/{model}";
    public ModelPreset With(
        string? model = null,
        string? url = null,
        string? provider = null,
        ImmutableDictionary<string, object>? extraBody = null,
        bool? enableSearch = null,
        bool? supportImageInput = null,
        float? temperature = null,
        bool? storeInDict = true
        )
    {
        return new(
             model ?? this.model,
             url ?? this.url,
             provider ?? this.provider,
             extraBody ?? this.extraBody,
             enableSearch ?? this.enableSearch,
             supportImageInput ?? this.supportImageInput,
             temperature ?? this.temperature,
             storeInDict ?? false
             );

    }
    public ModelPreset SavedWith(
        string? model = null,
        string? url = null,
        string? provider = null,
        ImmutableDictionary<string, object>? extraBody = null,
        bool? enableSearch = null,
        bool? supportImageInput = null,
        float? temperature = null
        )
    {
        return With(model,
             url,
             provider,
             extraBody,
             enableSearch,
             supportImageInput,
             temperature,
             storeInDict: true
             );

    }
    /// <summary>
    /// 根据模型名称获取模型预设
    /// </summary>
    /// <param name="name">模型名称，格式为 provider/model</param>
    /// <returns>模型预设</returns>
    public static ModelPreset? GetModelByName(string? name)
    {
        if (name == null)
        {
            return null;
        }
        if (modelsByName.TryGetValue(name, out var v))
        {
            return v;
        }
        return null;
    }
    public static void DisplayAllModels()
    {
        Console.WriteLine("Available Models");
        foreach (var item in modelsByName)
        {
            Console.Write($"{item.Value.model} By {item.Value.provider};");
        }
        Console.WriteLine();
    }
    public static string AllModels()
    {
        StringBuilder sb = new();
        foreach (var item in modelsByName)
        {
            sb.Append($"{item.Value.model} By {item.Value.provider};");
        }
        sb.AppendLine();
        return sb.ToString();
    }
    public static ImmutableDictionary<string, ModelPreset> AllModelsDict => modelsByName.ToImmutableDictionary();
    public string ApiTokenDictKey => $"ai-token-{provider}";



    public static readonly ModelPreset Glm_4_5_Free = new ModelPreset(
            model: "GLM-4.5-Flash",
            url: "https://open.bigmodel.cn/api/paas/v4",
            provider: "zhipu",
            extraBody: Empty.Add("thinking", Empty.Add("type", "enabled"))
        );
    public static readonly ModelPreset GLM_4_6V_Flash_Free = Glm_4_5_Free.SavedWith("GLM-4.6V-Flash", supportImageInput: true);
    public static readonly ModelPreset Glm_4V_Flash_Free = GLM_4_6V_Flash_Free.SavedWith("GLM-4V-Flash");
    public static readonly ModelPreset Glm_4_1V_Flash_Free = Glm_4V_Flash_Free.SavedWith("GLM-4.1V-Thinking-Flash");
    public static readonly ModelPreset Glm_4_Free = Glm_4_5_Free.SavedWith("GLM-4-Flash-250414");
    public static readonly ModelPreset Glm_4_7 = Glm_4_5_Free.SavedWith("GLM-4.7", extraBody: Empty);
    public static readonly ModelPreset Glm_4_7_Flash_Free = Glm_4_5_Free.SavedWith("glm-4.7-flash");
    public static readonly ModelPreset DeepSeekChat = new ModelPreset(
            "deepseek-v4-flash",
            "https://api.deepseek.com",
            "deepseek",
            extraBody: Empty.Add("thinking", Empty.Add("type", "enabled"))
        );
    public static readonly ModelPreset DeepSeekReasoner = DeepSeekChat.SavedWith(model: "deepseek-v4-pro");
    public static readonly ModelPreset Qwen3Max = new(
            "qwen3-max",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "ali",
            Empty.Add("enable_search", true)
        );
    public static readonly ModelPreset Qwen3Plus = Qwen3Max.SavedWith("qwen-plus-latest");
    public static readonly ModelPreset XiaomiMimoV2 = new(
        "mimo-v2-flash",
        "https://api.xiaomimimo.com/v1",
        "xiaomi"
        );
    public static readonly ModelPreset MiniMax3 = new(
        "MiniMax-M3",
        "https://api.minimax.chat/v1",
        "minimax"
        );
}

public record DashscopeModelPreset(
    string model,
    string url,
    string provider
    )
{
    public string ApiTokenDictKey => $"ai-token-{provider}";
    public string ImageGenerateUrl => $"{url}/services/aigc/multimodal-generation/generation";
    public static DashscopeModelPreset QwenImageMax = new(
        "qwen-image-max",
        "https://dashscope.aliyuncs.com/api/v1",
        "ali"
        );
}