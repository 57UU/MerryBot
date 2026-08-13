namespace LlmService;

public interface Backend
{
    public (GenerateResponse, TokenUsage) Generate(CancellationToken cancellationToken, IEnumerable<Message> messages, LlmOptions options);
}

public record LlmOptions();
public class GenerateResponse
{
    string content;
    ToolCall[] toolCalls;
    string reasoningContent;

}
public class ToolCall
{
    string id;
    string name;
    string arguements;
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
}
public record TokenUsage(
    int totalUsage,
    int promptUsage,
    int completionUsage
    );