using CommonLib;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading.Tasks;
using ZhipuClient;

namespace ZhipuClient;

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
    public readonly ImmutableDictionary<string, object> extraBody;
    private static ImmutableDictionary<string, object> Empty = ImmutableDictionary<string, object>.Empty;

    private static readonly Dictionary<string, ModelPreset> modelsByName = new();
    public string CompletionUrl => $"{url}/chat/completions";
    public ModelPreset(string model, string url, string provider, ImmutableDictionary<string, object>? extraBody = null, bool enableSearch=true,bool supportImageInput=false)
    {
        this.model = model;
        this.url = url;
        this.provider = provider;
        this.extraBody = extraBody ?? Empty;
        this.enableSearch = enableSearch;
        this.supportImageInput = supportImageInput;
        modelsByName[model] = this;
    }
    public ModelPreset With(
        string? model = null,
        string? url = null,
        string? provider = null,
        ImmutableDictionary<string, object>? extraBody = null,
        bool? enableSearch=null,
        bool? supportImageInput=null
        )
    {
        return new(
             model ?? this.model,
             url ?? this.url,
             provider ?? this.provider,
             extraBody ?? this.extraBody,
             enableSearch ?? this.enableSearch,
             supportImageInput ?? this.supportImageInput
             );

    }
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
            Console.Write($"{item.Key} By {item.Value.provider};");
        }
        Console.WriteLine();
    }
    public static string AllModels()
    {
        StringBuilder sb = new();
        foreach (var item in modelsByName)
        {
            sb.Append($"{item.Key} By {item.Value.provider};");
        }
        sb.AppendLine();
        return sb.ToString();
    }
    public string ApiTokenDictKey => $"ai-token-{provider}";



    public static readonly ModelPreset Glm_4_5_Free = new ModelPreset(
            model: "GLM-4.5-Flash",
            url: "https://open.bigmodel.cn/api/paas/v4",
            provider: "zhipu",
            extraBody: Empty.Add("thinking","enabled")
        );
    public static readonly ModelPreset GLM_4_6V_Free = Glm_4_5_Free.With("GLM-4.6V-Flash",supportImageInput:true);
    public static readonly ModelPreset Glm_4_Free = Glm_4_5_Free.With("GLM-4-Flash-250414");
    public static readonly ModelPreset Glm_4_7 = Glm_4_5_Free.With("GLM-4.7",extraBody:Empty);
    public static readonly ModelPreset Glm_4_7_Flash_Free = Glm_4_5_Free.With("glm-4.7-flash");
    public static readonly ModelPreset DeepSeekChat = new ModelPreset(
            "deepseek-chat",
            "https://api.deepseek.com",
            "deepseek"
        );
    public static readonly ModelPreset Qwen3Max = new(
            "qwen3-max",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "ali",
            Empty.Add("enable_search",true)
        );
    public static readonly ModelPreset Qwen3Plus = Qwen3Max.With("qwen-plus-latest");
    public static readonly ModelPreset XiaomiMimoV2 = new(
        "mimo-v2-flash",
        "https://api.xiaomimimo.com/v1",
        "xiaomi"
        );
}