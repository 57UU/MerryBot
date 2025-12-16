using CommonLib;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    public readonly ImmutableDictionary<string, object> extraBody;
    private static ImmutableDictionary<string, object> Empty = ImmutableDictionary<string, object>.Empty;

    private static readonly Dictionary<string, ModelPreset> modelsByName = new();
    public ModelPreset(string model, string url, string provider, ImmutableDictionary<string, object>? extraBody = null, bool enableSearch=true)
    {
        this.model = model;
        this.url = url;
        this.provider = provider;
        this.extraBody = extraBody ?? Empty;
        this.enableSearch = enableSearch;
        modelsByName[model] = this;
    }
    public ModelPreset With(
        string? model = null,
        string? url = null,
        string? provider = null,
        ImmutableDictionary<string, object>? extraBody = null,
        bool? enableSearch=null
        )
    {
        return new(
             model ?? this.model,
             url ?? this.url,
             provider ?? this.provider,
             extraBody ?? this.extraBody,
             enableSearch ?? this.enableSearch
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
    public string ApiTokenDictKey => $"ai-token-{provider}";



    public static readonly ModelPreset Glm_4_5_Free = new ModelPreset(
        model: "GLM-4.5-Flash",
        url: "https://open.bigmodel.cn/api/paas/v4/chat/completions",
        provider: "zhipu",
        extraBody: Empty.Add("thinking","enabled")
        );
    public static readonly ModelPreset Glm_4_Free = Glm_4_5_Free.With("GLM-4-Flash-250414");
    public static readonly ModelPreset Glm_4_6 = Glm_4_5_Free.With("GLM-4.6",extraBody:Empty);
    public static readonly ModelPreset DeepSeekChat = new ModelPreset(
        "deepseek-chat",
        "https://api.deepseek.com/chat/completions",
        "deepseek"
        );
    public static readonly ModelPreset Qwen3Max = new(
        "qwen3-max",
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
        "ali",
        Empty.Add("enable_search",true),
        enableSearch:false
        );
    public static readonly ModelPreset Qwen3Plus = Qwen3Max.With("qwen-plus-latest");
}