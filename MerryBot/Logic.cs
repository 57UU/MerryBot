using BotPlugin;
using CommonLib;
using DataProvider;
using NapcatClient;
using NapcatClient.MessageType;
using System.Reflection;
using System.Runtime.InteropServices;
using Tomlyn.Model;

namespace MerryBot;

internal partial class Logic
{
    readonly BotClient botClient;
    private readonly DataProvider.PluginStorageDatabase PluginStorageDatabase;
    private readonly List<PluginInfo> plugins = new();
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public static long AuthorizedUser { get { return ConfigManager.Instance.AuthorizedUser; } }
    readonly string[] CommandLineArguments = Environment.GetCommandLineArgs();
    private MainPlugin? mainPlugin;

    private static List<long> QqGroupIDs
    {
        get
        {
            return ConfigManager.Instance.QqGroups;
        }
    }
    public Logic(BotClient botClient, string dbPath)
    {
        this.botClient = botClient;
        PluginStorageDatabase = new(dbPath);
        LoadPlugins();
        botClient.OnGroupMessageReceived += OnGroupMessageReceived;
        RegisterEventHandlers();
    }

    private void RegisterEventHandlers()
    {
        botClient.OnNoticeEventReceived += OnNoticeEventReceived;
        botClient.OnGroupUploadEventReceived += OnGroupUploadEventReceived;
        botClient.OnGroupAdminEventReceived += OnGroupAdminEventReceived;
        botClient.OnGroupDecreaseEventReceived += OnGroupDecreaseEventReceived;
        botClient.OnGroupIncreaseEventReceived += OnGroupIncreaseEventReceived;
        botClient.OnGroupBanEventReceived += OnGroupBanEventReceived;
        botClient.OnFriendAddEventReceived += OnFriendAddEventReceived;
        botClient.OnGroupRecallEventReceived += OnGroupRecallEventReceived;
        botClient.OnFriendRecallEventReceived += OnFriendRecallEventReceived;
        botClient.OnPokeEventReceived += OnPokeEventReceived;
        botClient.OnLuckyKingEventReceived += OnLuckyKingEventReceived;
        botClient.OnHonorEventReceived += OnHonorEventReceived;
        botClient.OnGroupMsgEmojiLikeEventReceived += OnGroupMsgEmojiLikeEventReceived;
        botClient.OnEssenceEventReceived += OnEssenceEventReceived;
        botClient.OnGroupCardEventReceived += OnGroupCardEventReceived;
    }
    bool IsTargeted(ReceivedGroupMessage data)
    {
        var chain = data.message;
        var selfId = data.self_id;
        bool isTargeted = false;
        if (chain[0] is AtData atData)
        {
            string target = atData.Qq;
            if (target == selfId.ToString())
            {
                isTargeted = true;
            }
        }
        return isTargeted;
    }
    public void MainPluginInvokeNotInGroup(long groupId, List<TypedMessage> chain, ReceivedGroupMessage data)
    {
        if (mainPlugin == null)
        {
            logger.Error("Main Plugin is not loaded!");
            return;
        }
        if (IsTargeted(data))
        {
            mainPlugin.OnMessageMentionedNotInGroup(groupId, CollectionsMarshal.AsSpan(chain)[1..], data);
        }
    }
    public event Action<ReceivedGroupMessage>? OnRawGroupMessageReceived;

    public void OnGroupMessageReceived(long groupId, List<TypedMessage> chain, ReceivedGroupMessage data)
    {
        if (chain.Count == 0)
        {
            return;
        }
        if (!QqGroupIDs.Contains(groupId))
        {
            MainPluginInvokeNotInGroup(groupId, chain, data);
            return;
        }
        ReadOnlySpan<TypedMessage> span = CollectionsMarshal.AsSpan(chain);
        bool isTargeted = false;
        long selfId = BotUtils.GetSelfId(data);
        logger.Info($"on message:{groupId}|{BotUtils.MessageChainToString(span)}");

        long senderId = data.sender.user_id;

        OnRawGroupMessageReceived?.Invoke(data);

        bool isIntercepted = false;
        foreach (var plugInfo in plugins)
        {
            foreach (var interceptor in plugInfo.Interop.Interceptors)
            {
                if (interceptor(data))
                {
                    isIntercepted = true;
                    break;
                }
            }
        }
        if (isIntercepted)
        {
            return;
        }

        isTargeted = IsTargeted(data);

        if (isTargeted)
        {
            // at消息
            OnGroupMessageMentioned(groupId, span[1..], data);
        }
        else
        {
            OnGroupMessageNotMentioned(groupId, span, data);
        }
        OnGroupMessage(groupId, span, data);
    }

    private static List<(Type type, PluginTag attribute)> FindPlugins()
    {
        List<(Type type, PluginTag attribute)> list = [];
        Assembly assembly = Assembly.GetAssembly(typeof(Plugin))!;
        foreach (Type type in assembly.GetTypes().Append(typeof(MainPlugin))) // add MainPlugin
        {
            PluginTag attribute = type.GetCustomAttribute<PluginTag>()!;
            if (attribute != null && !attribute.IsIgnore)
            {
                list.Add((type, attribute));
            }
        }
        return list;
    }
    private IEnumerable<Action>? _pluginsDisposeActions;
    private void LoadPlugins()
    {
        var allPlugins = FindPlugins();
        //sort by priority
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
                // 获取或创建该插件的命名空间字典
                if (!ConfigManager.Instance.Variables.TryGetValue(attribute.Id, out var pluginVars))
                {
                    pluginVars = new TomlTable();
                    ConfigManager.Instance.Variables[attribute.Id] = pluginVars;
                }
                var interop = new PluginInterop(
                        new PluginLogger(attribute.Id),
                        QqGroupIDs,
                        () => plugins,
                        new PluginStorage(
                            (s) => PluginStorageDatabase.StorePluginData(attribute.Id, s),
                            () => PluginStorageDatabase.GetPluginData(attribute.Id)
                            ),
                        botClient,
                        pluginVars,
                        Shutdown,
                        AuthorizedUser,
                        CommandLineArguments,
                        ConfigManager.Save,
                        botClient.PathPrefix,
                        f => OnRawGroupMessageReceived += f
                        );
                pluginInteropMap.Add(type, interop);
                if (type == typeof(MainPlugin))
                {
                    pluginInitializer.AddDependency(type, new List<object> { this, interop });
                }
                else
                {
                    pluginInitializer.AddDependency(type, new List<object> { interop });
                }

            }
            catch (Exception ex)
            {
                logger.Error(ex, $"the plugin {attribute.Id} can not be loaded");
            }
        }
        //initialize
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
        mainPlugin = pluginInitializer.GetInstance<MainPlugin>();

        //加载插件的OnLoaded函数
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
    /// <summary>
    /// save data and shutdown
    /// </summary>
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
