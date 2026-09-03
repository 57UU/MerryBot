using CommonLib;

namespace BotPlugin;

[ConfigDescription("Agent 配置", "控制群聊 Agent 的模型选择、提示词和上下文策略。")]
public class AgentConfig : IPluginConfig
{
    [ConfigDescription("主模型", "Agent 使用的主模型 ID。")]
    public string LlmModel { get; set; } = "opencode-go/deepseek-v4-flash";

    [ConfigDescription("系统提示词", "发送给主模型的系统提示词。")]
    public string AiPrompt { get; set; } = "你是一个乐于助人、回答简洁的群聊助手。";

    [ConfigDescription("最大迭代次数", "单次请求允许的最大工具调用迭代次数。")]
    public int MaxIterations { get; set; } = 20;

    [ConfigDescription("上下文压缩比例", "上下文达到模型窗口的此比例后开始压缩。")]
    public double ContextCompactRatio { get; set; } = 0.7;

    [ConfigDescription("视觉模型列表", "主模型不支持视觉时依次使用的辅助视觉模型 ID 列表；按顺序逐个尝试，某个失效自动切换到下一个；留空则禁用。")]
    public List<string> VisionLlmModels { get; set; } = [];

    [ConfigDescription("视觉提示词", "交给辅助视觉模型的图片描述提示词。")]
    public string VisionPrompt { get; set; } = "请详细描述这张图片的内容。";

    [ConfigDescription("会话空闲淘汰时长", "群聊 Agent 会话空闲超过该时长（小时，支持小数，如 0.5）后自动清理，释放内存；配置非正数时回退默认值。")]
    public double IdleSessionTimeoutHours { get; set; } = 12;

    [ConfigDescription("允许执行 shell 命令", "是否注册 bash/终端工具集。默认关闭；开启后模型可在常驻 shell 中执行任意命令，请确认信任该群的用户。")]
    public bool AllowShell { get; set; } = false;

    [ConfigDescription("shell 运行用户", "AllowShell 开启后，shell 命令以该 Linux 用户身份（sudo -u user）执行；留空则以机器人进程所属用户执行。仅 Linux 生效")]
    public string? ShellUser { get; set; }

    [ConfigDescription("图片大小上限", "load_image 等工具允许下载的图片大小上限（MB）。")]
    public int MaxImageSizeMb { get; set; } = 10;

    [ConfigDescription("单轮并发工具调用上限", "模型单次迭代中并行执行的工具调用数上限，防止一次请求触发过多并发工具导致资源/成本失控。")]
    public int MaxConcurrentToolCalls { get; set; } = 4;

    [ConfigDescription("子任务数上限", "同时运行中的子 Agent 任务数上限，防止子任务（每个=一次完整 LLM 调用）无限堆积导致成本失控。")]
    public int MaxSubagents { get; set; } = 3;

    [ConfigDescription("后台 shell 任务上限", "同时运行中的后台 shell 任务数上限，防止 LLM 派发大量后台进程耗尽系统资源。")]
    public int MaxBackgroundTasks { get; set; } = 5;

    private int _maxReferenceDepth = 3;
    [ConfigDescription("引用消息自动展开深度", "自动将引用消息内容展开到上下文并以 <reference> 包裹；0 表示关闭，默认 3，范围 0-10。")]
    public int MaxReferenceDepth
    {
        get => _maxReferenceDepth;
        set => _maxReferenceDepth = Math.Clamp(value, 0, 10);
    }

    [ConfigDescription("自动水群总开关", "实验性功能：开启后白名单群内的非 @ 消息会被旁观攒批，定时投给 Agent；关闭则保持现有仅 @ 响应行为。")]
    public bool AutoChatEnable { get; set; } = false;

    [ConfigDescription("自动水群白名单", "启用自动水群的群号列表；为空时不旁观任何群。")]
    public List<long> AutoChatGroups { get; set; } = [];

    private int _autoChatBatchSize = 10;
    [ConfigDescription("攒批条数", "旁观消息攒够该条数即触发一次 Agent，范围 2-50。")]
    public int AutoChatBatchSize
    {
        get => _autoChatBatchSize;
        set => _autoChatBatchSize = Math.Clamp(value, 2, 50);
    }

    private int _autoChatFlushSeconds = 60;
    [ConfigDescription("攒批超时", "首条旁观消息到达后经过该秒数即触发一次 Agent（不够条数也触发），范围 10-600 秒。")]
    public int AutoChatFlushSeconds
    {
        get => _autoChatFlushSeconds;
        set => _autoChatFlushSeconds = Math.Clamp(value, 10, 600);
    }

    private int _autoChatMaxSendsPerTrigger = 3;
    [ConfigDescription("单批发送上限", "一次水群触发最多允许调用 send_message 发送的条数，范围 1-5。")]
    public int AutoChatMaxSendsPerTrigger
    {
        get => _autoChatMaxSendsPerTrigger;
        set => _autoChatMaxSendsPerTrigger = Math.Clamp(value, 1, 5);
    }

    [ConfigDescription("模拟发送", "开启后 send_message 只记日志不真正发群，用于灰度观察模型想回什么；确认行为符合预期后再关闭。")]
    public bool AutoChatDryRun { get; set; } = true;
}
