using LlmBackend;
using NapcatClient.MessageType;
using System.Text;

namespace BotPlugin;

/// <summary>插件可读取的、已经本地化的消息快照。</summary>
public sealed record ProcessedMessage(
    long GroupId,
    long MessageId,
    long SenderId,
    string SenderNickname,
    string SenderGroupNickname,
    string SenderGroupRole,
    IReadOnlyList<TypedMessage> MessageChain,
    DateTime Time,
    bool IsDeleted);

/// <summary>插件可读取的、已经本地化的合并转发快照。</summary>
public sealed record ProcessedForwardMessage(
    string Id,
    long SourceGroupId,
    IReadOnlyList<ProcessedMessage> Messages,
    DateTime Time);

/// <summary>本地资源的内容与元数据。</summary>
public sealed record LocalMessageResource(
    string LocalUri,
    string Kind,
    string? OriginalName,
    string? ContentType,
    byte[] Data);

/// <summary>
/// Core 提供的消息查询入口。所有返回的消息链只包含本地引用，不泄漏远端 URL。
/// </summary>
public interface IMessageService
{
    Task<ProcessedMessage?> GetMessageAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default);
    Task<ProcessedMessage?> GetReplyAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default);
    Task<ProcessedForwardMessage?> GetForwardAsync(string forwardIdOrReference, long sourceGroupId, CancellationToken cancellationToken = default);
    Task<LocalMessageResource?> GetResourceAsync(string localUri, CancellationToken cancellationToken = default);

    /// <summary>分页查询群聊历史消息（按时间倒序，第 1 页为最新消息；已撤回的消息不返回）。</summary>
    Task<IReadOnlyList<ProcessedMessage>> GetGroupMessagesAsync(long groupId, int page, int pageSize, CancellationToken cancellationToken = default);
    /// <summary>群聊历史消息总数（含撤回消息）。</summary>
    Task<int> GetGroupMessageCountAsync(long groupId, CancellationToken cancellationToken = default);

    /// <summary>记录一条 AI 会话消息到审计历史（仅文本内容，可带 token 用量）。messageType 为 user/assistant/tool。</summary>
    Task RecordAiMessageAsync(string sessionKey, string messageType, string content, TokenUsage usage);
}

/// <summary>处理链中使用的稳定本地 URI。</summary>
public static class LocalMessageReference
{
    public const string Scheme = "merrybot";

    public static string Message(long groupId, long messageId) => $"{Scheme}://message/{groupId}/{messageId}";
    public static string Forward(string forwardId) => $"{Scheme}://forward/{Encode(forwardId)}";
    public static string Resource(string kind, string hash) => $"{Scheme}://resource/{kind}/{hash}";

    public static bool TryParseMessage(string value, out long groupId, out long messageId)
    {
        groupId = 0;
        messageId = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Scheme || uri.Host != "message") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && long.TryParse(parts[0], out groupId) && long.TryParse(parts[1], out messageId);
    }

    public static bool TryParseForward(string value, out string forwardId)
    {
        forwardId = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Scheme || uri.Host != "forward") return false;
        var encoded = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(encoded)) return false;
        try
        {
            forwardId = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsResource(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Scheme && uri.Host == "resource";

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
