using CommonLib;

namespace ZhipuClient;

public partial class ZhipuAi
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
    /// 最大上下文长度
    /// </summary>
    public int SlidingWindowContext { get; set; } = 30;
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
