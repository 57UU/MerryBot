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

    [ConfigDescription("视觉模型", "主模型不支持视觉时使用的辅助视觉模型 ID；留空则禁用。")]
    public string VisionLlmModel { get; set; } = string.Empty;

    [ConfigDescription("视觉提示词", "交给辅助视觉模型的图片描述提示词。")]
    public string VisionPrompt { get; set; } = "请详细描述这张图片的内容。";

    [ConfigDescription("允许执行 shell 命令", "是否注册 bash/终端工具集。默认关闭；开启后模型可在常驻 shell 中执行任意命令，请确认信任该群的用户。")]
    public bool AllowShell { get; set; } = false;

    [ConfigDescription("图片大小上限", "load_image 等工具允许下载的图片大小上限（MB）。")]
    public int MaxImageSizeMb { get; set; } = 10;
}
