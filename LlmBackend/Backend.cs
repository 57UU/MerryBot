using System.Text.Json;

namespace LlmBackend;
public enum BackendType
{
    ChatCompletion,
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
    public string text;
}
public class MessagePartImage : MessagePart
{
    public string image;
}

public class Message
{
    public Role role;
    public IEnumerable<MessagePart> content;
    //tool response
    public string toolCallId;
    //assistant response
    public IEnumerable<ToolCall> toolCalls;

    public string reasoningContent;
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
    );