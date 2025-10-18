using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MerryBot;

public class Config
{
    public static string SettingFile = "setting.json";
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
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        var json = JsonSerializer.Serialize(instance, options: options);
        await Utils.write(SettingFile, json);
    }
    public async static Task load()
    {
        JsonSerializerOptions options = new JsonSerializerOptions()
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        var json = await Utils.read(SettingFile);
        var i = JsonSerializer.Deserialize<Config>(json, options);
        //foreach (var k in i.Variables.Keys)
        //{
        //    var v =(JsonElement) i.Variables[k];
        //    i.Variables[k] = JsonNode.Parse(v.GetRawText());
        //}
        instance = i;

    }
    public string napcat_server = "ws://<host>:<port>/";
    public string napcat_token = "napcat";
    public List<long> qq_groups = [];
    [JsonPropertyName("variables")]
    public Dictionary<string, dynamic> Variables = new();
}
