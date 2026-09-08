using Agent.Session;
using BotPlugin;
using MerryBot.Contracts;

namespace MerryBot.WebUI.Api;

/// <summary>Bot 连接状态（由主程序在注册表填充时通过工厂提供，避免 WebUI 依赖 NapcatClient）。</summary>
public sealed record BotStatusDto(
    bool Connected,
    string SelfId,
    string Nickname,
    string NapcatAddress);

/// <summary>概览页状态：Bot 连接 + 群聊数 + 系统运行信息 + git 版本信息（Home 页直连拼装）。</summary>
public sealed record SystemStatusDto(
    BotStatusDto Bot,
    int GroupCount,
    string OsVersion,
    string Framework,
    string MachineName,
    int ProcessId,
    double UptimeSeconds,
    long WorkingSetBytes,
    long GcMemoryBytes,
    string GitInfo);

/// <summary>
/// WebUI 直连注册表：Blazor 组件跑在 server，与宿主同进程，经 DI 拿到它后直接调用进程内服务，
/// 不再经 JS fetch + HTTP Minimal API 回调自己。
/// 宿主按两步填充：CreateApp 时注册空壳（configureServices），LoadPlugins 后填实例。
/// 插件未加载 / 独立运行（Program.Main 无插件）时对应字段为 null，页面应展示“服务不可用”。
/// 字段只写一次、此后只读，volatile 保证跨线程可见性。
/// </summary>
public sealed class WebUiServiceRegistry
{
    private volatile ConfigRegistry? config;
    private volatile Func<BotStatusDto>? botStatusProvider;
    private volatile IHostLifecycle? hostLifecycle;
    private volatile IGroupManager? groupManager;
    private volatile ClockService? clock;
    private volatile LogFileService? logFiles;
    private volatile ModelsDevCatalogService? catalog;
    private volatile string? botPathPrefix;
    private volatile Action<int>? shutdown;
    private volatile ILlmProviderManagementService? llmProviders;
    private volatile ISkillManagementService? skills;
    private volatile IMemoryManagementService? memories;
    private volatile IPromptOverrideService? promptOverrides;
    private volatile IAgentSessionControlService? sessionControl;

    /// <summary>可编辑配置注册表（core 拥有）。</summary>
    public ConfigRegistry? Config
    {
        get => config;
        set => config = value;
    }

    /// <summary>Bot 连接状态工厂（闭包捕获 BotClient，不让 WebUI 依赖 NapcatClient）。</summary>
    public Func<BotStatusDto>? BotStatusProvider
    {
        get => botStatusProvider;
        set => botStatusProvider = value;
    }

    /// <summary>进程生命周期服务（版本/更新/重启/重载/退出）。</summary>
    public IHostLifecycle? HostLifecycle
    {
        get => hostLifecycle;
        set => hostLifecycle = value;
    }

    /// <summary>群启用管理（主程序实现，直接操作 core 配置）。</summary>
    public IGroupManager? GroupManager
    {
        get => groupManager;
        set => groupManager = value;
    }

    /// <summary>core 拥有的定时任务调度器。</summary>
    public ClockService? Clock
    {
        get => clock;
        set => clock = value;
    }

    /// <summary>日志文件读取（NLog 落盘文件，与 /logs 页展示逻辑同体）。</summary>
    public LogFileService? LogFiles
    {
        get => logFiles;
        set => logFiles = value;
    }

    /// <summary>models.dev 目录服务（本地缓存优先）。</summary>
    public ModelsDevCatalogService? Catalog
    {
        get => catalog;
        set => catalog = value;
    }

    /// <summary>机器人数据目录（catalog 缓存等按此定位，正常应与 Catalog 一起设置）。</summary>
    public string? BotPathPrefix
    {
        get => botPathPrefix;
        set => botPathPrefix = value;
    }

    /// <summary>进程退出入口（Logic.Shutdown，退出码语义见 ExitCode）。</summary>
    public Action<int>? Shutdown
    {
        get => shutdown;
        set => shutdown = value;
    }

    /// <summary>LLM Provider/模型/Key 管理（llm-provider 插件，未加载为 null）。</summary>
    public ILlmProviderManagementService? LlmProviders
    {
        get => llmProviders;
        set => llmProviders = value;
    }

    /// <summary>Skill 管理（agent-service 插件，未加载为 null）。</summary>
    public ISkillManagementService? Skills
    {
        get => skills;
        set => skills = value;
    }

    /// <summary>记忆管理（agent-service 插件，未加载为 null）。</summary>
    public IMemoryManagementService? Memories
    {
        get => memories;
        set => memories = value;
    }

    /// <summary>提示词复写管理（agent-service 插件，未加载为 null）。</summary>
    public IPromptOverrideService? PromptOverrides
    {
        get => promptOverrides;
        set => promptOverrides = value;
    }

    /// <summary>会话忙闲/清除控制（agent 插件持有 AgentSessionManager，未加载为 null）。</summary>
    public IAgentSessionControlService? SessionControl
    {
        get => sessionControl;
        set => sessionControl = value;
    }
}
