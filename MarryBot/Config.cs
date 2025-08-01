using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarryBot;

public class Config
{
    public const string SETTING = "setting.json";
    public static Config instance { get; private set; }
    public async static Task Initialize()
    {
        try
        {
            await load();
            await save();
        }
        catch (Exception)
        {
            instance = new Config();
            save().Wait();
        }

    }
    public async static Task save()
    {
        JsonSerializerOptions options = new JsonSerializerOptions()
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        var json = JsonSerializer.Serialize(instance, options: options);
        await Utils.write(SETTING, json);
    }
    public async static Task load()
    {
        JsonSerializerOptions options = new JsonSerializerOptions()
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        var json = await Utils.read(SETTING);
        var i = JsonSerializer.Deserialize<Config>(json, options);
        instance = i;

    }
    public string napcat_server = "ws://<host>:<port>/";
    public string napcat_token = "napcat";
    public List<long> qq_groups = [];
    public Dictionary<string, dynamic> Variables = new();


}
