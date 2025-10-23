using BotPlugin;
using CommonLib;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MerryBot;

internal class Logic
{
    readonly BotClient botClient;
    private DataProvider.PluginStorageDatabase PluginStorageDatabase = new();
    private List<PluginInfo> plugins = new();
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public long AuthorizedUser { get {return Config.Instance.AuthorizedUser; } }
    private List<long> qqGroupIDs {
        get {
            return Config.Instance.qq_groups;
        }
    }
    public Logic(BotClient botClient)
    {
        this.botClient = botClient;
        LoadPlugins();
        botClient.OnGroupMessageReceived += OnGroupMessageReceived;
    }

    public void OnGroupMessageReceived(long groupId,List<Message> chain, ReceivedGroupMessage data)
    {
        if (!qqGroupIDs.Contains(groupId))
        {
            return;
        }
        if (chain.Count == 0)
        {
            return;
        }
        
        ReadOnlySpan<Message> span=CollectionsMarshal.AsSpan(chain);
        bool isTargeted = false;
        long selfId = BotUtils.GetSelfId(data);
        logger.Info($"on message:{groupId}|{BotUtils.MessageChainToString(span)}");

        long senderId= data.sender.user_id;
        bool isIntercepted = false;
        foreach(var plugInfo in plugins)
        {
            foreach(var interceptor in plugInfo.Interop.Interceptors)
            {
                if (interceptor(data))
                {
                    isIntercepted = true;
                }
            }
        }
        if (isIntercepted)
        {
            return;
        }

        if (chain[0].MessageType == "at")
        {
            string target = chain[0].Data["qq"];
            if (target == selfId.ToString())
            {
                isTargeted = true;
            }
        }

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
    private void OnGroupMessageMentioned(long groupId, ReadOnlySpan<Message> chain, ReceivedGroupMessage data)
    {
        foreach(var i in plugins)
        {
            if (!i.Instance.IsEnable)
            {
                //if the plugin is not enable, skip it
                continue;
            }
            try
            {
                i.Instance.OnGroupMessageMentioned(groupId, chain, data);
            }
            catch (Exception e) { 
                logger.Warn(e);
            }
            
        }
    }
    private void OnGroupMessageNotMentioned(long groupId, ReadOnlySpan<Message> chain, ReceivedGroupMessage data)
    {
        foreach (var i in plugins)
        {
            if (!i.Instance.IsEnable)
            {
                //if the plugin is not enable, skip it
                continue;
            }
            try
            {
                i.Instance.OnGroupMessageNotMentioned(groupId, chain, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }
        }
    }
    private void OnGroupMessage(long groupId, ReadOnlySpan<Message> chain, ReceivedGroupMessage data)
    {
        foreach (var i in plugins)
        {
            if (!i.Instance.IsEnable)
            {
                //if the plugin is not enable, skip it
                continue;
            }
            try
            {
                i.Instance.OnGroupMessage(groupId, chain, data);
            }
            catch (Exception e)
            {
                logger.Warn(e);
            }
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
        var allPlugins= FindPlugins();
        //sort by priority
        allPlugins.Sort((a, b) => {
            return a.attribute.Priority.CompareTo(b.attribute.Priority);
        });
        foreach (var (type,attribute) in allPlugins) {
            try
            {
                Type[] constructorParameterTypes = [typeof(PluginInterop)];
                logger.Debug($"find plugin {attribute.Name}");
                ConstructorInfo constructorInfo = type.GetConstructor(constructorParameterTypes)
                    ?? throw new PluginNotUsableException("can not find specific constructor"); ;
                var interop = new PluginInterop(
                        new PluginLogger(attribute.Name),
                        qqGroupIDs,
                        () => plugins,
                        new PluginStorage(
                            (s) => PluginStorageDatabase.StorePluginData(attribute.Name, s),
                            () => PluginStorageDatabase.GetPluginData(attribute.Name)
                            ),
                        botClient,
                        Config.Instance.Variables,
                        Shutdown,
                        AuthorizedUser
                        );
                // 创建构造函数参数数组
                object[] constructorParameters = [interop];

                // 使用构造函数创建对象
                Plugin pluginInstance = (Plugin)constructorInfo!.Invoke(constructorParameters);
                plugins.Add(
                    new PluginInfo(
                        pluginInstance,
                        attribute,
                        interop
                        )
                    );

            }
            catch (PluginNotUsableException ex)
            {
                logger.Warn($"the plugin {attribute.Name} can not be loaded, {ex.Message}");
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException;
                if (inner is PluginNotUsableException)
                {
                    logger.Warn($"the plugin {attribute.Name} can not be loaded: {inner.Message}");
                }
                else
                {
                    logger.Error(ex, $"the plugin {attribute.Name} can not be loaded");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"the plugin {attribute.Name} can not be loaded");
            }
        }

        //加载插件的OnLoaded函数
        foreach (var i in plugins)
        {
            i.Instance.OnLoaded();
        }
    }
    /// <summary>
    /// save data
    /// </summary>
    public void Shutdown(int exitCode)
    {
        PluginStorageDatabase.Close();
        botClient.Close();
        NLog.LogManager.Flush();
        Environment.Exit(exitCode);
    }
}
class PluginLogger(string tag) : ISimpleLogger
{
    private readonly NLog.Logger _logger = NLog.LogManager.GetLogger($"plugin:{tag}");

    public void Debug(string message)
    {
        _logger.Debug(message);
    }
    public void Trace(string message)
    {
        _logger.Trace(message);
    }

    public void Error(string message)
    {
        _logger.Error(message);
    }

    public void Fatal(string message)
    {
        _logger.Fatal(message);
    }

    public void Info(string message)
    {
        _logger.Info(message);
    }

    public void Warn(string message)
    {
        _logger.Warn(message);
    }
}
