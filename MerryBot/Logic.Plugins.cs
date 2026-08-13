using BotPlugin;
using CommonLib;
using System.Reflection;
using Tomlyn.Model;

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
        allPlugins.Sort((a, b) =>
        {
            return a.attribute.Priority.CompareTo(b.attribute.Priority);
        });
        PluginInitializer<Plugin> pluginInitializer = new();
        Dictionary<Type, PluginInterop> pluginInteropMap = new();
        logger.Debug($"find plugin: {string.Join(",", allPlugins.Select(p => p.attribute.Id))}");
        foreach (var (type, attribute) in allPlugins)
        {
            try
            {
                if (!ConfigManager.Instance.Variables.TryGetValue(attribute.Id, out var pluginVars))
                {
                    pluginVars = new TomlTable();
                    ConfigManager.Instance.Variables[attribute.Id] = pluginVars;
                }
                var pluginStorage = new PluginStorage(
                            (s) => PluginStorageDatabase.StorePluginData(attribute.Id, s),
                            () => PluginStorageDatabase.GetPluginData(attribute.Id),
                            (groupId, s) => PluginStorageDatabase.StoreGroupPluginData(attribute.Id, groupId, s),
                            groupId => PluginStorageDatabase.GetGroupPluginData(attribute.Id, groupId)
                            );
                var interop = new PluginInterop(
                new PluginLogger(attribute.Id),
                QqGroupIDs,
                () => plugins,
                pluginStorage,
                botClient,
                pluginVars,
                Shutdown,
                AuthorizedUser,
                CommandLineArguments,
                ConfigManager.Save,
                botClient.PathPrefix,
                EventRegister
                );
                pluginInteropMap.Add(type, interop);
                pluginInitializer.AddDependency(type, [interop]);

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
    }

    public void Shutdown(int exitCode = 0)
    {
        foreach (var dispose in _pluginsDisposeActions!)
        {
            dispose();
        }
        PluginStorageDatabase.Dispose();
        botClient.Close();
        NLog.LogManager.Shutdown();
        Environment.Exit(exitCode);
    }
}
