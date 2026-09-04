using MerryBot.Contracts;
using DataProvider;

namespace MerryBot;

public static class ConfigManager
{
    private static PluginStorageDatabase _db = null!;
    public static Config Instance
    {
        get
        {
            if (field == null)
            {
                throw new Exception("Config is not initialized!");
            }
            return field;
        }
        private set { field = value; }
    }
    /// <summary>保护 QqGroups 列表的并发读写（WS 消息线程与 WebUI 线程）。</summary>
    private static readonly Lock _groupsLock = new();
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    /// <summary>从插件存储数据库加载核心配置；不存在时落库默认配置，类型不匹配时仅使用内存默认值。</summary>
    public async static Task Initialize(PluginStorageDatabase db)
    {
        _db = db;
        try
        {
            var loaded = await _db.GetPluginConfig("config", prefix: "core");
            if (loaded == null)
            {
                Instance = new Config();
                await Save();
            }
            else if (loaded is Config config)
            {
                Instance = config;
            }
            else
            {
                logger.Warn("配置类型不匹配（{0}），使用内存默认值", loaded.GetType().Name);
                Instance = new Config();
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "加载配置失败，使用默认配置");
            Instance = new Config();
            try
            {
                await Save();
            }
            catch (Exception saveEx)
            {
                logger.Error(saveEx, "保存默认配置失败");
            }
        }
    }
    public async static Task Save()
    {
        await _db.SetPluginConfig("config", Instance, prefix: "core");
    }

    /// <summary>返回启用群列表的线程安全快照。</summary>
    public static IReadOnlyList<long> GetGroupIdsSnapshot()
    {
        _groupsLock.Enter();
        try
        {
            return Instance.QqGroups.ToArray();
        }
        finally
        {
            _groupsLock.Exit();
        }
    }

    /// <summary>线程安全地判断群组是否已启用。</summary>
    public static bool ContainsGroup(long groupId)
    {
        _groupsLock.Enter();
        try
        {
            return Instance.QqGroups.Contains(groupId);
        }
        finally
        {
            _groupsLock.Exit();
        }
    }

    /// <summary>线程安全地添加群组并持久化（与配置保存共用同一序列化路径）。</summary>
    public static async Task AddGroupAsync(long groupId)
    {
        bool changed = false;
        _groupsLock.Enter();
        try
        {
            var groups = Instance.QqGroups;
            if (!groups.Contains(groupId))
            {
                groups.Add(groupId);
                changed = true;
            }
        }
        finally
        {
            _groupsLock.Exit();
        }
        if (changed)
        {
            await Save();
        }
    }

    /// <summary>线程安全地移除群组并持久化（与配置保存共用同一序列化路径）。</summary>
    public static async Task RemoveGroupAsync(long groupId)
    {
        bool changed = false;
        _groupsLock.Enter();
        try
        {
            changed = Instance.QqGroups.Remove(groupId);
        }
        finally
        {
            _groupsLock.Exit();
        }
        if (changed)
        {
            await Save();
        }
    }
}
[ConfigDescription("核心配置", "MerryBot 的连接、群组和运行编号设置。")]
public class Config
{
    [ConfigDescription("Napcat 服务地址", "Napcat WebSocket 服务的地址，例如 ws://localhost:3001/")]
    public string NapcatServer { set; get; } = "ws://localhost:3001/";
    [ConfigDescription("Napcat Token", "连接 Napcat WebSocket 服务时使用的认证 Token。")]
    public string NapcatToken { set; get; } = "napcat";
    [ConfigDescription("监听群组", "需要接收和处理消息的 QQ 群号列表。")]
    public List<long> QqGroups { set; get; } = [];
    [ConfigDescription("授权用户", "拥有管理权限的 QQ 号。")]
    public long AuthorizedUser { set; get; } = -1;

    [ConfigDescription("机器编号", "历史记录使用的机器编号；小于 0 时首次启动自动生成 0 到 31 的编号。")]
    public int MachineCode { set; get; } = -1;

    [ConfigDescription("资源大小限制", "下载并保存的图片/文件大小上限（MB）。")]
    public int ResourceSizeLimitMb { set; get; } = 20;

    [ConfigDescription("重连间隔", "消息适配器断开后重试连接的间隔（秒）。")]
    public int ReconnectIntervalSeconds { set; get; } = 15;
}
