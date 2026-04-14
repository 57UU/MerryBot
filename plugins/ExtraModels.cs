using System;
using System.Collections.Generic;
using System.Text;
using Tomlyn;
using Tomlyn.Serialization;
using OpenAiClient;

namespace BotPlugin;

[PluginTag("extra-models", "extra models", "加载自定义模型", priority: 1000, type: PluginType.Background)]
public class ExtraModels : Plugin
{
    public ExtraModels(PluginInterop interop) : base(interop)
    {
        options = new()
        {
            MetadataStore = tomlMetadataStore,
            WriteIndented = true,
            IndentSize = 3
        };
        _loadExtraModelPreset();
    }

    private readonly TomlMetadataStore tomlMetadataStore = new();
    private readonly TomlSerializerOptions options;

    private void _loadExtraModelPreset()
    {
        string filePath = Path.Combine(Interop.PathPrefix, "extra-models.toml");
        if (!File.Exists(filePath))
        {
            var defaultConfig = new ExtraModelPresetConfig
            {
                Models = new List<ExtraModelItem>
                {
                    new ExtraModelItem
                    {
                        Model = "example-model",
                        Url = "https://api.example.com/v1",
                        Provider = "example",
                        IsEnabled = false
                    }
                }
            };

            string toml = TomlSerializer.Serialize(defaultConfig, options);
            File.WriteAllText(filePath, toml);
            Logger.Info($"Created default extra models config at {filePath}");
            return;
        }

        try
        {
            using var content = File.OpenRead(filePath);
            var config = TomlSerializer.Deserialize<ExtraModelPresetConfig>(content, options);
            if (config?.Models != null)
            {
                foreach (var item in config.Models)
                {
                    if (item.IsEnabled)
                    {
                        _ = new ModelPreset(
                            model: item.Model,
                            url: item.Url,
                            provider: item.Provider,
                            enableSearch: item.EnableSearch,
                            supportImageInput: item.SupportImageInput
                        );
                        Logger.Info($"Loaded extra model: {item.Model} from {filePath}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load extra models from {filePath}: {ex.Message}");
        }
    }
}
class ExtraModelPresetConfig
{
    public List<ExtraModelItem> Models { get; set; } = new();
}
class ExtraModelItem
{
    public string Model { get; set; } = "";
    public string Url { get; set; } = "";
    public string Provider { get; set; } = "";
    public bool EnableSearch { get; set; } = true;
    public bool SupportImageInput { get; set; } = false;
    public bool IsEnabled { get; set; } = false;
}