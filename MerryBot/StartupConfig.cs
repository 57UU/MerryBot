using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MerryBot;

/// <summary>
/// 启动配置（setting.toml，内容为 YAML 语法）。
/// 本文件中的配置项是程序启动所必需的（如 WebUI 监听地址），
/// 不在 WebUI 中提供修改入口，避免"WebUI 挂了就改不回来"的引导问题。
/// 修改后需重启 MerryBot 生效。
/// </summary>
public static class StartupConfig
{
    public const string FileName = "setting.toml";
    public const string DefaultWebAddress = "http://localhost:5000";

    private static readonly Lock _lock = new();
    // 与 Agent.Tui（TuiConfigStore）一致：连字符命名约定 + 忽略未知键
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static string _webAddress = DefaultWebAddress;

    /// <summary>WebUI 监听地址（来自 setting.toml 的 web-address，默认 http://localhost:5000）。</summary>
    public static string WebAddress => _webAddress;

    /// <summary>
    /// 加载数据目录下的 setting.toml；文件不存在时生成带注释的默认模板。
    /// 解析失败或值非法时回退默认值，保证进程可启动。幂等，可重复调用。
    /// </summary>
    public static void Load(string dataPath)
    {
        lock (_lock)
        {
            // 每次加载都从默认值开始，文件缺失/解析失败/未命中键时即使用默认值
            _webAddress = DefaultWebAddress;
            var path = Path.Combine(dataPath, FileName);
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultTemplate);
                    Console.WriteLine($"startup config not found, default template written:{path}");
                    return;
                }

                var data = Deserializer.Deserialize<StartupConfigData>(File.ReadAllText(path));
                if (data?.WebAddress is { Length: > 0 } address
                    && Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    _webAddress = address;
                }
                else
                {
                    Console.WriteLine($"setting.toml: invalid web-address \"{data?.WebAddress}\", fallback to {DefaultWebAddress}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"setting.toml: parse failed ({ex.Message}), fallback to {DefaultWebAddress}");
            }
        }
    }

    private sealed class StartupConfigData
    {
        public string? WebAddress { get; set; }
    }

    private static string DefaultTemplate => """
        # MerryBot 启动配置
        # 本文件中的配置项是程序启动所必需的，不在 WebUI 中提供修改入口。
        # 修改后需重启 MerryBot 生效。

        # WebUI 监听地址（默认 http://localhost:5000）
        web-address: "http://localhost:5000"
        """;
}
