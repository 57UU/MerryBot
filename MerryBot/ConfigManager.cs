using MerryBot.Api;
using Tomlyn;
using Tomlyn.Model;
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
public class Config
{
    [TomlPropertyName("napcat-server")]
    [ConfigRule(Required = true, Scheme = "ws,wss")]
    public string NapcatServer { set; get; } = "ws://<host>:<port>/";
    [TomlPropertyName("napcat-token")]
    [ConfigRule(Required = true)]
    public string NapcatToken { set; get; } = "napcat";
    [TomlPropertyName("qq-groups")]
    [ConfigRule(Positive = true)]
    public List<long> QqGroups { set; get; } = [];
    [TomlPropertyName("authorized-user")]
    [ConfigRule(Positive = true)]
    public long AuthorizedUser { set; get; } = -1;
    [TomlPropertyName("variables")]
    public Dictionary<string, TomlTable> Variables { set; get; } = new();
}