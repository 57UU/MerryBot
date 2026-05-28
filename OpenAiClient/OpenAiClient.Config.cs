using CommonLib;

namespace OpenAiClient;

public partial class OpenAiCompatible
{

    /// <summary>
    /// 日志记录器
    /// </summary>
    public ISimpleLogger Logger { set; private get; } = ConsoleLogger.Instance;
    /// <summary>
    /// 最大网页内容长度
    /// </summary>
    public int MaxWebContentLength { get; set; } = 5000;
    /// <summary>
    /// 用于总结网页内容的轻量总结器，null 则直接返回原始内容
    /// </summary>
    public WebviewSummarizer? WebviewSummarizer { get; set; } = null;
    /// <summary>
    /// 最大上下文长度（Legacy 滑动窗口模式的消息数阈值）
    /// </summary>
    public int SlidingWindowContext { get; set; } = 30;
    /// <summary>
    /// 是否启用自动压缩，禁用时回退到滑动窗口删除
    /// </summary>
    public bool AutoCompressEnabled { get; set; } = true;
    /// <summary>
    /// 触发压缩的 token 数阈值（本地字符估算，字符数/2）
    /// </summary>
    public int CompressTokenThreshold { get; set; } = 64_000;
    /// <summary>
    /// 用于压缩/摘要的模型，null 则使用主模型
    /// </summary>
    public ModelPreset? CompressionModel { get; set; } = null;
    /// <summary>
    /// 压缩模型的 Token，null 则使用主客户端的 Token
    /// </summary>
    public string? CompressionToken { get; set; } = null;
    /// <summary>
    /// 响应超时时间
    /// </summary>
    public int ResponseTimeout
    {
        get
        {
            return field;
        }
        set
        {
            field = value;
            client.Timeout = TimeSpan.FromSeconds(value);
        }
    } = 20;
    public HistoryRecorder? HistoryRecorder { get; set; } = null;
    /// <summary>
    /// 是否使用动态提示 ，启用后，将会在prompt中插入工具提示
    /// </summary>
    public bool UseDynamicPrompt { get; set; } = true;
    //constants
    public const string SYSTEM = "system";
    public const string USER = "user";
    public const string ASSISTANT = "assistant";
    public const string TOOL = "tool";
    public const string STOP = "stop";
    public const string TOOL_CALL = "tool_calls";
    public const string LENGTH = "length";
    public const string SENSITIVE = "sensitive";
    public const string NETWORK_ERROR = "network_error";
}
