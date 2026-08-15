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
        if (config is null)
        {
            // 无已存配置：生成默认配置并落盘
            config = (IPluginConfig)Activator.CreateInstance(configType)!;
            PluginStorageDatabase.SetPluginConfig(pluginId, config).GetAwaiter().GetResult();
        }
        else if (!configType.IsInstanceOfType(config))
        {
            // 存储配置类型与插件期望类型不匹配：本次仅内存使用默认值并记录 WARN，
            // 不写回存储，避免用默认配置覆盖已存数据
            logger.Warn("插件 {0} 的已存配置类型 {1} 与期望类型 {2} 不匹配，本次使用默认配置（不写回存储）",
                pluginId, config.GetType().Name, configType.Name);
            config = (IPluginConfig)Activator.CreateInstance(configType)!;
        }
        configRegistry.RegisterConfig(
            $"plugin:{pluginId}",
            config,
            () => PluginStorageDatabase.SetPluginConfig(pluginId, config));
        return config;
    }
}
