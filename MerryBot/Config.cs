using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MerryBot;

public class Config
{
    public static string SettingFile = "setting.json";
    public static Config Instance {
        get {
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
            await load();
            await save();
        }
        catch (Exception)
        {
            Instance = new Config();
            save().Wait();
        }
    }
    static readonly JsonSerializerOptions settingOptions = new JsonSerializerOptions()
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
    public async static Task save()
    {

        var json = JsonSerializer.Serialize(Instance, options: settingOptions);
        await Utils.write(SettingFile, json);
    }
    public async static Task load()
    {

        var json = await Utils.read(SettingFile);
        Config i = JsonSerializer.Deserialize<Config>(json!, settingOptions)!;
        //foreach (var k in i.Variables.Keys)
        //{
        //    var v =(JsonElement) i.Variables[k];
        //    i.Variables[k] = JsonNode.Parse(v.GetRawText());
        //}
        Instance = i;

    }
    [JsonPropertyName("napcat_server")]
    public string NapcatServer = "ws://<host>:<port>/";
    [JsonPropertyName("napcat_token")]
    public string NapcatToken = "napcat";
    [JsonPropertyName("qq_groups")]
    public List<long> QqGroups = [];
    [JsonPropertyName("authorized-user")]
    public long AuthorizedUser = -1;
    [JsonPropertyName("variables")]
    public Dictionary<string, dynamic> Variables = new();
}
