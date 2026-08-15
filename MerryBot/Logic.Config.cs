using BotPlugin;

namespace MerryBot;

internal partial class Logic
{

    private IPluginConfig GetConfig(string pluginId, Type configType)
    {
        var config = PluginStorageDatabase
            .GetPluginConfig(pluginId)
            .GetAwaiter()
            .GetResult() as IPluginConfig;
        if (config is null || !configType.IsInstanceOfType(config))
        {
            //generate default config
            config = (IPluginConfig)Activator.CreateInstance(configType)!;
            //store default config
            PluginStorageDatabase.SetPluginConfig(pluginId, config).GetAwaiter().GetResult();
        }
        configRegistry.RegisterConfig(
            $"plugin:{pluginId}",
            config,
            () => PluginStorageDatabase.SetPluginConfig(pluginId, config));
        return config;
    }
}
