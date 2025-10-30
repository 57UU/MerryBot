using CommonLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZhipuClient;

namespace ZhipuClient;

public class ModelPreset
{
    public string model;
    public bool thinking;
    public string url;
    public string provider;
    private static readonly Dictionary<string, ModelPreset> modelsByName = new();
    public ModelPreset(string model, bool thinking,string url= "https://open.bigmodel.cn/api/paas/v4/chat/completions",string provider="zhipu")
    {
        this.model = model;
        this.thinking = true;
        this.url = url;
        this.provider = provider;
        modelsByName[model] = this;
    }
    public static ModelPreset? GetModelByName(string? name)
    {
        if (name == null)
        {
            return null;
        }
        if(modelsByName.TryGetValue(name, out var v))
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
    


    public static readonly ModelPreset Glm_4_5_Free =new ModelPreset("GLM-4.5-Flash", true);
    public static readonly ModelPreset Glm_4_Free=new ModelPreset("GLM-4-Flash-250414", true);
    public static readonly ModelPreset Glm_4_6 = new ModelPreset("GLM-4.6", false);
    public static readonly ModelPreset DeepSeekChat= new ModelPreset("deepseek-chat", false, "https://api.deepseek.com/chat/completions","deepseek");
}