using MerryBot.Contracts;
using DataProvider;
using LiteDB;
using LiteDB.Async;

namespace BotPlugin;

/// <summary>
/// 读取 Agent 当前内存上下文快照（context_histories 集合）。与 ai_messages 审计日志不同：
/// 上下文会随压缩/重置变化，反映 Agent 当前实际"看到"的对话内容。
/// 直接以 BsonDocument 读取，避免暴露 DatabaseContextHistory 的私有持久化类型。
/// 只读无状态；宿主（Logic）可独立创建实例并注册到 WebUI DI，供组件直接注入。
/// </summary>
public sealed class ContextSnapshotService : IContextSnapshotService
{
    private readonly ILiteCollectionAsync<BsonDocument> _snapshots;

    public ContextSnapshotService(PluginDatabaseScope database)
    {
        ArgumentNullException.ThrowIfNull(database);
        // 与 DatabaseContextHistory 同一集合：agent 插件作用域下的 context_histories
        _snapshots = database.GetCollection<BsonDocument>("context_histories");
    }

    public async Task<IReadOnlyList<ContextSnapshotSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var all = await _snapshots.FindAllAsync();
        return all
            .Select(doc => new ContextSnapshotSession(
                doc["_id"].AsString,
                doc["Messages"].AsArray.Count,
                ToDateTimeOffset(doc["UpdatedAtUtc"].AsDateTime)))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToList();
    }

    public async Task<ContextSnapshotDetail?> GetSnapshotAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        var doc = await _snapshots.FindByIdAsync(sessionKey);
        if (doc is null) return null;
        return ParseSnapshot(doc);
    }

    private static ContextSnapshotDetail ParseSnapshot(BsonDocument doc)
    {
        var messages = new List<ContextMessageEntry>();
        foreach (var raw in doc["Messages"].AsArray)
        {
            var msg = raw.AsDocument;

            // StoredMessagePartKind 是 enum，LiteDB 默认序列化为字符串 "Text"/"Image"
            var content = string.Join('\n', msg["Content"].AsArray
                .Select(part =>
                {
                    var p = part.AsDocument;
                    var kind = p["Kind"];
                    var isImage = kind.IsString ? kind.AsString == "Image" : kind.AsInt32 == 1;
                    return isImage ? "[图片]" : p["Value"].AsString;
                })
                .Where(s => !string.IsNullOrEmpty(s)));

            var toolCalls = msg["ToolCalls"].AsArray
                .Select(tc => new ContextToolCallEntry(
                    tc.AsDocument["Name"].AsString,
                    tc.AsDocument["Arguments"].AsString))
                .ToList();

            messages.Add(new ContextMessageEntry(
                msg["Role"].AsString,
                content,
                msg["ToolCallId"].AsString,
                toolCalls,
                msg["ReasoningContent"].AsString));
        }

        return new ContextSnapshotDetail(
            doc["_id"].AsString,
            messages,
            ToDateTimeOffset(doc["UpdatedAtUtc"].AsDateTime));
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
