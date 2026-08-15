using BotPlugin;
using CommonLib;
using MerryBot.WebUI.Api;
using System.Reflection;

namespace MerryBot;

internal partial class Logic
{
    private readonly List<PluginInfo> plugins = new();
    private IEnumerable<Action>? _pluginsDisposeActions;

    private static List<long> QqGroupIDs
    {
        get
        {
            return ConfigManager.Instance.QqGroups;
        }
    }

    private static List<(Type type, PluginTag attribute)> FindPlugins()
    {
        List<(Type type, PluginTag attribute)> list = [];
        Assembly assembly = Assembly.GetAssembly(typeof(Plugin))!;
        foreach (Type type in assembly.GetTypes())
        {
            PluginTag attribute = type.GetCustomAttribute<PluginTag>()!;
            if (attribute != null && !attribute.IsIgnore)
            {
                list.Add((type, attribute));
            }
        }
        return list;
    }

    private void LoadPlugins()
    {
        var allPlugins = FindPlugins();
        PluginInitializer<Plugin> pluginInitializer = new(GetConfig);
        Dictionary<Type, PluginInterop> pluginInteropMap = new();
        logger.Debug($"find plugin: {string.Join(",", allPlugins.Select(p => p.attribute.Id))}");
        foreach (var (type, attribute) in allPlugins)
        {
            try
            {
                var pluginStorage = new PluginStorage(
                            (s) => PluginStorageDatabase.StorePluginData(attribute.Id, s),
                            () => PluginStorageDatabase.GetPluginData(attribute.Id),
                            PluginStorageDatabase.CreateScope(attribute.Id)
                            );
                var interop = new PluginInterop(
                new PluginLogger(attribute.Id),
                QqGroupIDs,
                () => plugins,
                pluginStorage,
                botClient.Bot,
                Shutdown,
                AuthorizedUser,
                botClient.PathPrefix,
                EventRegister,
                messageService
                );
                pluginInteropMap.Add(type, interop);
                pluginInitializer.AddDependency(type, attribute, [interop]);

            }
            catch (Exception ex)
            {
                logger.Error(ex, $"the plugin {attribute.Id} can not be loaded");
            }
        }
        try
        {
            pluginInitializer.InitializeAll();
        }
        catch (Exception e)
        {
            logger.Fatal(e);
        }
        _pluginsDisposeActions = pluginInitializer.GetDisposeActions();
        IEnumerable<(Plugin? pluginInstance, PluginTag attribute)> allPluginInstance
            = allPlugins.Select(p => (pluginInitializer.GetInstance(p.type), p.attribute));
        foreach (var (pluginInstance, attribute) in allPluginInstance)
        {
            if (pluginInstance != null)
            {
                plugins.Add(
                    new PluginInfo(
                    pluginInstance,
                    attribute,
                    pluginInteropMap[pluginInstance.GetType()]
                    )
                );
            }

        }
        foreach (var i in plugins)
        {
            i.Instance.OnLoaded().ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    logger.Error($"the plugin {i.PluginTag.Id} OnLoaded failed: {task.Exception}");
                }
            });
        }
        RegisterWebUi();
    }
    void RegisterWebUi()
    {
        var llmProviderManager = plugins
    .Select(static plugin => plugin.Instance)
    .OfType<ILlmProviderManagementService>()
    .SingleOrDefault();
        if (llmProviderManager == null)
        {
            logger.Warn("LLM Provider 插件未加载，未注册 LLM Provider Web API。");
        }
        else
        {
            LlmProviderApiMapper.Map(webUiApplication, llmProviderManager, botClient.PathPrefix);
        }

        var skillManager = plugins
            .Select(static plugin => plugin.Instance)
            .OfType<ISkillManagementService>()
            .SingleOrDefault();
        if (skillManager == null)
        {
            logger.Warn("Agent Skill 管理服务未加载，未注册 Skill Web API。");
        }
        else
        {
            SkillApiMapper.Map(webUiApplication, skillManager);
        }

        var memoryManager = plugins
            .Select(static plugin => plugin.Instance)
            .OfType<IMemoryManagementService>()
            .SingleOrDefault();
        if (memoryManager == null)
        {
            logger.Warn("Agent 记忆管理服务未加载，未注册记忆 Web API。");
        }
        else
        {
            MemoryApiMapper.Map(webUiApplication, memoryManager, historyRecorder);
        }
    }

    public void Shutdown(int exitCode = 0)
    {
        foreach (var dispose in _pluginsDisposeActions!)
        {
            dispose();
        }
        webUiApplication.StopAsync().GetAwaiter().GetResult();
        webUiApplication.DisposeAsync().AsTask().GetAwaiter().GetResult();
        historyRecorder.Dispose();
        PluginStorageDatabase.Dispose();
        botClient.Close();
        NLog.LogManager.Shutdown();
        Environment.Exit(exitCode);
    }
}
