using System.Text.Json;

namespace LlmBackend;
public enum BackendType
{
    ChatCompletion,
}

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

public interface Backend
{
    public Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options);
}

public record LlmOptions(
    string? Model = null,
    float? Temperature = null,
    int? MaxTokens = null,
    IEnumerable<ToolDef>? Tools = null,
    IDictionary<string, object>? ExtraBody = null
    );
public class GenerateResponse
{
    public string? Content { get; }
    public ToolCall[]? ToolCalls { get; }
    public string? ReasoningContent { get; }

    public GenerateResponse(string? content, ToolCall[]? toolCalls, string? reasoningContent)
    {
        Content = content;
        ToolCalls = toolCalls;
        ReasoningContent = reasoningContent;
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
    public static Message User(string text) => new Message
    {
        role = Role.User,
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
