using CommonLib;
using Tomlyn;
using Tomlyn.Serialization;

namespace MerryBot;

public static class ConfigManager
{
    public static string SettingFile = "setting.toml";
    public static Config Instance
    {
        get
        {
            if (field == null)
            {
                throw new Exception("Config is not initialized!");
            }
            return field;
        }
        private set { field = value; }
    }
    public async static Task Initialize()
    {
        try
        {
            await Load();
            await Save();
        }
        catch (Exception)
        {
            Instance = new Config();
            Save().Wait();
        }
    }
    private static TomlMetadataStore configTomlMetadata = new TomlMetadataStore();
    private static TomlSerializerOptions _tomlModelOptions = new()
    {
        MetadataStore = configTomlMetadata
    };
    public async static Task Save()
    {

        var toml = TomlSerializer.Serialize(Instance, options: _tomlModelOptions);
        await Utils.write(SettingFile, toml);
    }
    public async static Task Load()
    {

        var json = await Utils.read(SettingFile);
        Config i = TomlSerializer.Deserialize<Config>(json!, options: _tomlModelOptions)!;
        Instance = i;

    }
}
[ConfigDescription("核心配置", "MerryBot 的连接、群组、运行编号和 WebUI 监听设置。")]
public class Config
{
    [TomlPropertyName("napcat-server")]
    [ConfigDescription("Napcat 服务地址", "Napcat WebSocket 服务的地址，例如 ws://localhost:8080/")]
    public string NapcatServer { set; get; } = "ws://<host>:<port>/";
    [TomlPropertyName("napcat-token")]
    [ConfigDescription("Napcat Token", "连接 Napcat WebSocket 服务时使用的认证 Token。")]
    public string NapcatToken { set; get; } = "napcat";
    [TomlPropertyName("qq-groups")]
    [ConfigDescription("监听群组", "需要接收和处理消息的 QQ 群号列表。")]
    public List<long> QqGroups { set; get; } = [];
    [TomlPropertyName("authorized-user")]
    [ConfigDescription("授权用户", "拥有管理权限的 QQ 号。")]
    public long AuthorizedUser { set; get; } = -1;

    [TomlPropertyName("machine-code")]
    [ConfigDescription("机器编号", "历史记录使用的机器编号；小于 0 时首次启动自动生成 0 到 31 的编号。")]
    public int MachineCode { set; get; } = -1;

    [TomlPropertyName("web-address")]
    [ConfigDescription("WebUI 地址", "WebUI 监听的 HTTP 或 HTTPS 地址。")]
    public string WebAddress { set; get; } = "http://localhost:5000";
}
