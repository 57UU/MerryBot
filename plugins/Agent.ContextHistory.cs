using Agent;
using DataProvider;
using LiteDB;
using LiteDB.Async;
using LlmBackend;

namespace BotPlugin;

/// <summary>
/// Persists the model's recoverable working context for one session. The record
/// is intentionally an explicit storage projection instead of serializing the
/// LLM runtime types directly.
/// </summary>
internal sealed class DatabaseContextHistory : ContextHistory
{
    private readonly string sessionId;
    private readonly ILiteCollectionAsync<ContextSnapshotRecord> snapshots;

    public DatabaseContextHistory(PluginDatabaseScope database, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        this.sessionId = sessionId;
        snapshots = database.GetCollection<ContextSnapshotRecord>("context_histories");
    }

    public static Task EnsureInitializedAsync(PluginDatabaseScope database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database
            .GetCollection<ContextSnapshotRecord>("context_histories")
            .EnsureIndexAsync(snapshot => snapshot.UpdatedAtUtc);
    }

    public async Task<IList<Message>> Restore()
    {
        var snapshot = await snapshots.FindByIdAsync(sessionId);
        return snapshot == null
            ? []
            : snapshot.Messages.Select(ToMessage).ToList();
    }

    public Task Append(IList<Message> value) => Replace(value);

    public async Task Replace(IList<Message> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        await snapshots.UpsertAsync(new ContextSnapshotRecord
        {
            SessionId = sessionId,
            Messages = value.Select(ToRecord).ToList(),
            UpdatedAtUtc = DateTime.UtcNow,
        });
    }

    public async Task Clear()
    {
        await snapshots.DeleteAsync(sessionId);
    }

    private static StoredMessage ToRecord(Message message) => new()
    {
        Role = message.role.Value,
        Content = message.content.Select(ToRecord).ToList(),
        ToolCallId = message.toolCallId,
        ToolCalls = message.toolCalls.Select(toolCall => new StoredToolCall
        {
            Id = toolCall.Id,
            Name = toolCall.Name,
            Arguments = toolCall.Arguments,
        }).ToList(),
        ReasoningContent = message.reasoningContent,
    };

    private static StoredMessagePart ToRecord(MessagePart part) => part switch
    {
        MessagePartText text => new StoredMessagePart { Kind = StoredMessagePartKind.Text, Value = text.text },
        MessagePartImage image => new StoredMessagePart { Kind = StoredMessagePartKind.Image, Value = image.image },
        _ => throw new NotSupportedException($"不支持持久化消息分段类型: {part.GetType().FullName}"),
    };

    private static Message ToMessage(StoredMessage message) => new()
    {
        role = ParseRole(message.Role),
        content = message.Content.Select(ToMessagePart).ToList(),
        toolCallId = message.ToolCallId,
        toolCalls = message.ToolCalls.Select(toolCall => new ToolCall(
            toolCall.Id,
            toolCall.Name,
            toolCall.Arguments)).ToList(),
        reasoningContent = message.ReasoningContent,
    };

    private static MessagePart ToMessagePart(StoredMessagePart part) => part.Kind switch
    {
        StoredMessagePartKind.Text => new MessagePartText { text = part.Value },
        StoredMessagePartKind.Image => new MessagePartImage { image = part.Value },
        _ => throw new InvalidDataException($"未知的持久化消息分段类型: {part.Kind}"),
    };

    private static Role ParseRole(string value) => value switch
    {
        "user" => Role.User,
        "assistant" => Role.Assistant,
        "system" => Role.System,
        "tool" => Role.Tool,
        _ => throw new InvalidDataException($"未知的持久化消息角色: {value}"),
    };

    private sealed class ContextSnapshotRecord
    {
        [BsonId] public string SessionId { get; set; } = string.Empty;
        public List<StoredMessage> Messages { get; set; } = [];
        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class StoredMessage
    {
        public string Role { get; set; } = "user";
        public List<StoredMessagePart> Content { get; set; } = [];
        public string ToolCallId { get; set; } = string.Empty;
        public List<StoredToolCall> ToolCalls { get; set; } = [];
        public string ReasoningContent { get; set; } = string.Empty;
    }

    private sealed class StoredMessagePart
    {
        public StoredMessagePartKind Kind { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private enum StoredMessagePartKind
    {
        Text,
        Image,
    }

    private sealed class StoredToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
    }
}
