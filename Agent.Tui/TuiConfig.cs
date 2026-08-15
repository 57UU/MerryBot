using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agent.Tui;

/// <summary>
/// TUI 的 YAML 配置根。明文存放 API Key（实验性质，不做加密）。
/// schema 见 <see cref="ProviderConfig"/> / <see cref="ActiveSelection"/>。
/// </summary>
public sealed class TuiConfig
{
    public ActiveSelection Active { get; set; } = new();
    public List<ProviderConfig> Providers { get; set; } = [];

    public ProviderConfig? FindProvider(string? id) =>
        string.IsNullOrEmpty(id) ? null : Providers.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// 解析当前活动供应商与模型；若活动项无效则回退到首个可用供应商的首个模型。
    /// </summary>
    public (ProviderConfig? provider, string? model) ResolveActive()
    {
        var provider = FindProvider(Active.Provider);
        if (provider is not null && !string.IsNullOrEmpty(Active.Model)
            && provider.Models.Contains(Active.Model))
        {
            return (provider, Active.Model);
        }
        var first = Providers.FirstOrDefault();
        var model = first?.Models.FirstOrDefault();
        return (first, model);
    }
}

/// <summary>当前选中的供应商与模型。</summary>
public sealed class ActiveSelection
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
}

/// <summary>已配置的供应商（来源 models.dev 或内置 opencode）。</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApiBase { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public List<string> Models { get; set; } = [];
}

/// <summary>YAML 文件读写。失败时回退默认配置，保证进程可启动。</summary>
public static class TuiConfigStore
{
    public const string FileName = "tui-config.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .Build();

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, FileName);

    public static TuiConfig Load()
    {
        var path = Path;
        try
        {
            if (!File.Exists(path))
            {
                var seed = CreateDefault();
                Save(seed);
                return seed;
            }
            var yaml = File.ReadAllText(path);
            return Deserializer.Deserialize<TuiConfig>(yaml) ?? CreateDefault();
        }
        catch
        {
            // 实验性质：任何读写/反序列化失败都回退默认，不阻断启动
            return CreateDefault();
        }
    }

    public static void Save(TuiConfig cfg)
    {
        try
        {
            File.WriteAllText(Path, Serializer.Serialize(cfg));
        }
        catch
        {
            // 持久化失败不影响内存运行
        }
    }

    /// <summary>内置 opencode 供应商，保留原 Program.cs 的开箱即用体验。</summary>
    public static TuiConfig CreateDefault() => new()
    {
        Active = new ActiveSelection { Provider = "opencode", Model = "deepseek-v4-flash" },
        Providers =
        [
            new ProviderConfig
            {
                Id = "opencode",
                Name = "OpenCode Go",
                ApiBase = "https://opencode.ai/zen/go/v1",
                ApiKey = string.Empty,
                Models = ["deepseek-v4-flash"],
            },
        ],
    };
}
