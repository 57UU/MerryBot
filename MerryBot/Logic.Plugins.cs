using Agent.Session;
using BotPlugin;
using CommonLib;
using MerryBot.WebUI.Api;
using System.Reflection;

namespace MerryBot;

internal partial class Logic
{
    private readonly List<PluginInfo> plugins = new();
    private IEnumerable<Action>? _pluginsDisposeActions;

    private static IEnumerable<long> QqGroupIDs
    {
        get
        {
            return ConfigManager.GetGroupIdsSnapshot();
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
                            // agent-service 是 Agent 的对外服务面（Skill/记忆管理），
                            // 必须与 agent 共享同一数据库命名空间，否则存量记忆/历史数据会因集合名变化而“消失”
                            PluginStorageDatabase.CreateScope(attribute.Id == "agent-service" ? "agent" : attribute.Id)
                            );
                var interop = new PluginInterop(
                new PluginLogger(attribute.Id),
                QqGroupIDs,
                () => plugins,
                pluginStorage,
                hostLifecycle,
                AuthorizedUser,
                botClient.PathPrefix,
                EventRegister,
                messageService,
                new BotMessageChannel(botClient.Bot, new NLogAdapter(), attribute.Id),
                // 每个插件获得绑定自己 pluginId 的调度器门面：CRUD 与执行器注册自动限定在本插件任务内
                new ClockScope(clockService, attribute.Id)
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
            // InitializeAll 内部已按插件隔离依赖解析异常，此处仅作兜底
            logger.Error(e, "插件初始化异常");
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
            try
            {
                i.Instance.OnLoaded().ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        logger.Error($"the plugin {i.PluginTag.Id} OnLoaded failed: {task.Exception}");
                    }
                });
            }
            catch (Exception ex)
            {
                // 非 async 的 OnLoaded 同步抛异常时不允许逃出，避免中断后续插件的加载
                logger.Error(ex, $"the plugin {i.PluginTag.Id} OnLoaded failed synchronously");
            }
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

        var contextSnapshotManager = plugins
            .Select(static plugin => plugin.Instance)
            .OfType<IContextSnapshotService>()
            .SingleOrDefault();
        if (contextSnapshotManager == null)
        {
            logger.Warn("Agent 上下文快照服务未加载，未注册上下文快照 Web API。");
        }
        else
        {
            ContextSnapshotApiMapper.Map(webUiApplication, contextSnapshotManager, historyRecorder);
        }
    }

    private static int _shutdownTriggered;

    public void Shutdown(int exitCode = 0)
    {
        // 幂等保护：WebUI 重启与 Ctrl+C 并发触发时只执行一次完整关闭
        if (Interlocked.CompareExchange(ref _shutdownTriggered, 1, 0) != 0)
        {
            return;
        }
        foreach (var dispose in _pluginsDisposeActions!)
        {
            dispose();
        }
        // Ctrl+C/SIGTERM 会先被内嵌 WebUI 的 ConsoleLifetime 捕获并启动
        // StopAsync。此时再次同步调用 StopAsync 可能与 WebUI 自己的停止流程
        // 互相等待，导致整个进程无法退出。重载/更新则不会经过该信号路径，
        // 仍执行完整的同步停止与释放，以确保端口和资源及时释放。
        if (webUiLifetime.ApplicationStopping.IsCancellationRequested)
        {
            logger.Warn("WebUI 已在响应进程停止信号，跳过重复的同步 StopAsync");
        }
        else
        {
            webUiApplication.StopAsync().GetAwaiter().GetResult();
            webUiApplication.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        historyRecorder.Dispose();
        // 调度器在数据库释放前停止：等待运行中的定时任务（≤5s）收敛
        clockService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        PluginStorageDatabase.Dispose();
        _reconnectCts.Cancel();
        botClient.Close();
        NLog.LogManager.Shutdown();
        Environment.Exit(exitCode);
    }
}
