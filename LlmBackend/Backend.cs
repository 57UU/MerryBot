using System.Text.Json;

namespace LlmBackend;

/// <summary>模型的输入和推理能力，与具体 Provider 或客户端重试策略无关。</summary>
[Flags]
public enum LlmModelCapabilities
{
    None = 0,
    Text = 1 << 0,
    ImageInput = 1 << 1,
    AttachmentInput = 1 << 2,
    ToolCalls = 1 << 3,
    Reasoning = 1 << 4,
    StructuredOutput = 1 << 5,
}

/// <summary>请求超时的默认值，未在 LlmOptions 中指定时生效。</summary>
public static class LlmDefaults
{
    /// <summary>
    /// 首字节（首 token）超时，仅流式请求生效：衡量服务端产出第一个 chunk 的延迟。
    /// 非流式请求不设此段——服务端"算完整轮才发响应头"，TTFB 对非流式无意义，
    /// 且默认值远小于总时长时会误杀深度思考模型的长生成，只受 TotalGeneration 约束。
    /// </summary>
    public static readonly TimeSpan TimeToFirstByte = TimeSpan.FromSeconds(60);
    /// <summary>整个生成过程（含响应体读取）的总超时上限</summary>
    public static readonly TimeSpan TotalGeneration = TimeSpan.FromMinutes(5);
    /// <summary>流式生成的默认总超时：长输出常见，比一次性生成的默认值放宽</summary>
    public static readonly TimeSpan StreamingTotalGeneration = TimeSpan.FromMinutes(30);
}

public interface Backend
{
    public Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options);

    /// <summary>
    /// 流式生成：请求立即发出，增量推入 sink；返回的 Task 在流读完
    /// （OnCompleted 已回调）后完成，中途失败以归一化 LlmException 终结。
    /// 取消令牌为普通参数（调用方取消时抛 OperationCanceledException）。
    /// </summary>
    public Task GenerateStream(IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// 流式生成的推送式接收端：后端在 SSE 读循环上单线程顺序回调（不保证线程亲和），
/// 回调应是轻量非阻塞操作（事件转发/缓冲）。
/// 契约：OnCompleted 之后不再有任何回调；回调抛出的 LlmException
/// （如重试层检出正文标记）会穿透后端读循环向上传播，后端不得包装。
/// </summary>
public interface IStreamSink
{
    /// <summary>正文增量（逐 token 或逐块，按序拼接即为完整正文）。</summary>
    void OnTextDelta(string delta);

    /// <summary>推理增量（OpenAI reasoning_content / Anthropic thinking 文字 / Responses reasoning 摘要），按序拼接。</summary>
    void OnReasoningDelta(string delta);

    /// <summary>流正常结束：携带完整 GenerateResponse 与 TokenUsage（正文/推理/工具调用/thinking 块均为全量）。</summary>
    void OnCompleted(GenerateResponse response, TokenUsage usage);
}

public record LlmOptions(
    string? Model = null,
    float? Temperature = null,
    int? MaxTokens = null,
    IEnumerable<ToolDef>? Tools = null,
    IDictionary<string, object>? ExtraBody = null,
    string? ReasoningEffort = null,
    /// <summary>首字节（首 token）超时，仅流式请求生效；非流式只受 TotalTimeout 约束</summary>
    TimeSpan? TimeToFirstByte = null,
    TimeSpan? TotalTimeout = null
    )
{
    /// <summary>
    /// 返回与当前实例相同但禁用工具的副本（上下文压缩等纯文本摘要任务用）。
    /// 用 with 复制而非重建，避免丢失其余配置。
    /// </summary>
    public LlmOptions WithoutTools() => this with { Tools = null };
}
public class GenerateResponse
{
    public string? Content { get; }
    public ToolCall[]? ToolCalls { get; }
    public string? ReasoningContent { get; }

    /// <summary>
    /// Anthropic 格式的思考块（JSON 数组，元素为 {type:"thinking"|"redacted_thinking",
    /// thinking, signature, data}）。深度思考（extended thinking）返回的 thinking 块
    /// 带加密签名，tool calling 多轮中必须原样回传，否则 API 拒绝请求；
    /// 仅 anthropic 后端写入，其他格式恒为 null。
    /// </summary>
    public string? ThinkingBlocks { get; }

    public GenerateResponse(string? content, ToolCall[]? toolCalls, string? reasoningContent, string? thinkingBlocks = null)
    {
        Content = content;
        ToolCalls = toolCalls;
        ReasoningContent = reasoningContent;
        ThinkingBlocks = thinkingBlocks;
    }
}
public class ToolCall
{
    public string Id { get; }
    public string Name { get; }
    public string Arguments { get; }

    public ToolCall(string id, string name, string arguments)
    {
        Id = id;
        Name = name;
        Arguments = arguments;
    }
}
public class Role
{
    private Role(string value) { Value = value; }
    public string Value { get; private set; }
    public static Role User => new Role("user");
    public static Role Assistant => new Role("assistant");
    public static Role System => new Role("system");
    public static Role Tool => new Role("tool");
}
public class MessagePart
{

}
public class MessagePartText : MessagePart
{
    public string text = string.Empty;
}
public class MessagePartImage : MessagePart
{
    public string image = string.Empty;
}

public class Message
{
    public Role role = Role.User;
    public IEnumerable<MessagePart> content = [];
    //tool response
    public string toolCallId = string.Empty;
    //assistant response
    public IEnumerable<ToolCall> toolCalls = [];

    public string reasoningContent = string.Empty;

    /// <summary>
    /// Anthropic 思考块回放（JSON 数组，与 GenerateResponse.ThinkingBlocks 同格式）。
    /// 深度思考开启后，assistant 消息的 thinking 块（含加密签名）必须在后续轮次
    /// 原样回传，否则 API 拒绝请求；仅 anthropic 后端写入，其他格式恒为空。
    /// </summary>
    public string thinkingBlocks = string.Empty;

    public static Message User(string text) => new Message
    {
        role = Role.User,
        content = [new MessagePartText { text = text }]
    };
    public static Message System(string text) => new Message
    {
        role = Role.System,
        content = [new MessagePartText { text = text }]
    };

}

public record TokenUsage(
    int totalUsage,
    int promptUsage,
    int completionUsage,
    int cachedUsage = 0
    )
{
    public static TokenUsage Zero => new(0, 0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.totalUsage + b.totalUsage,
        a.promptUsage + b.promptUsage,
        a.completionUsage + b.completionUsage,
        a.cachedUsage + b.cachedUsage);
};
