using LiteDB;
using LiteDB.Async;

namespace DataService;

/// <summary>
/// <see cref="HistoryRecorder"/> 的 schema 迁移：基于 LiteDB UserVersion 依次执行未完成步骤，幂等。
/// </summary>
public partial class HistoryRecorder
{
    /// <summary>
    /// 数据库 schema 版本。每次新增迁移步骤时递增。
    /// - 0: 初始版本（未迁移）
    /// - 1: 修正 ForwardData.Content 旧格式（JsonElement? → List&lt;GroupMessage&gt;?）
    /// - 2: 补齐消息业务键并增加本地资源引用集合
    /// - 3: ai_messages 主键从 GroupId 改为 SessionKey（统一会话标识）
    /// - 4: 移除 MessageKey，统一使用 ObjectId Id
    /// </summary>
    private const int CurrentSchemaVersion = 4;

    /// <summary>
    /// 单个迁移步骤：从 <paramref name="FromVersion"/> 迁移到 FromVersion+1。
    /// 委托执行实际的数据变更，幂等执行无副作用。
    /// </summary>
    private record DbMigration(int FromVersion, string Name, Func<HistoryRecorder, Task> Action);

    /// <summary>
    /// 有序迁移步骤表。只追加新条目，不修改已有条目。
    /// 索引 i 的迁移将数据库从版本 i 升级到 i+1。
    /// </summary>
    private static readonly DbMigration[] Migrations =
    {
        new(0, "ForwardData.Content: JsonElement? → List<GroupMessage>?",
            static self => self.MigrateForwardDataContentV1Async()),
        new(1, "MessageKey and resource references",
            static self => self.MigrateMessageKeysV2Async()),
        new(2, "ai_messages: GroupId → SessionKey",
            static self => self.MigrateAiMessageSessionKeysV3Async()),
        new(3, "Remove MessageKey, use ObjectId Id",
            static self => self.MigrateMessageKeysV4Async()),
    };

    /// <summary>
    /// 执行数据库迁移。根据 LiteDB 的 UserVersion 字段判断当前版本，
    /// 依次执行未完成的迁移步骤，每完成一步立即写入新版本号。
    /// 幂等：已是最新版本时直接返回。
    /// </summary>
    public async Task MigrateAsync()
    {
        int current = database.UserVersion;
        for (int i = current; i < CurrentSchemaVersion; i++)
        {
            var step = Migrations[i];
            await step.Action(this);
            // 每完成一步立即持久化版本号，避免中途失败重复执行已完成的步骤
            database.UserVersion = i + 1;
        }
    }

    /// <summary>
    /// 迁移 v0 → v1：修正旧版 ForwardData.Content 字段。
    /// 旧版 Content 为 JsonElement?，LiteDB 以非 BsonArray 格式存储；
    /// 新版改为 List&lt;GroupMessage&gt;? 后旧数据无法反序列化。
    /// 迁移将所有非 BsonArray 的 Content 置空，允许新类型正常读取。
    /// </summary>
    private async Task MigrateForwardDataContentV1Async()
    {
        foreach (var colName in new[] { "messages", "forward_messages" })
        {
            var col = database.GetCollection(colName);
            var docs = await col.FindAllAsync();
            foreach (var doc in docs)
            {
                if (MigrateForwardDataContentRecursive(doc))
                {
                    await col.UpdateAsync(doc);
                }
            }
        }
    }

    private static bool MigrateForwardDataContentRecursive(BsonDocument doc)
    {
        bool changed = false;

        // 检查是否为 ForwardData（通过 _type 鉴别器识别）
        if (doc.TryGetValue("_type", out var typeVal) && typeVal.IsString
            && typeVal.AsString.Contains("ForwardData"))
        {
            if (doc.TryGetValue("Content", out var contentVal) && !contentVal.IsNull)
            {
                // 新类型 List<GroupMessage> 需要 BsonArray；非 BsonArray 的旧数据置空
                if (!contentVal.IsArray)
                {
                    doc["Content"] = BsonValue.Null;
                    changed = true;
                }
            }
        }

        // 递归遍历 Messages 数组（GroupMessage.Messages / ForwardMessageEntry.Messages）
        if (doc.TryGetValue("Messages", out var messagesVal) && messagesVal.IsArray)
        {
            foreach (var item in messagesVal.AsArray)
            {
                if (item.IsDocument && MigrateForwardDataContentRecursive(item.AsDocument))
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    private async Task MigrateMessageKeysV2Async()
    {
        // 旧 MessageKey 已废弃，v2 仅确保转发与资源索引
        await forwardMessagesCollection.EnsureIndexAsync(x => x.ForwardId, true);
        await resourceReferencesCollection.EnsureIndexAsync(x => x.LocalUri, true);
    }

    /// <summary>
    /// 迁移 v2 → v3：ai_messages 的 GroupId 改为统一会话标识 SessionKey。
    /// 旧文档按「qq/group/{群号}」补齐 SessionKey 并移除 GroupId。
    /// </summary>
    private async Task MigrateAiMessageSessionKeysV3Async()
    {
        var collection = database.GetCollection("ai_messages");
        var documents = await collection.FindAllAsync();
        foreach (var document in documents)
        {
            if (document.TryGetValue("SessionKey", out var sessionKey) && sessionKey.IsString && !string.IsNullOrEmpty(sessionKey.AsString))
            {
                continue;
            }
            if (!document.TryGetValue("GroupId", out var groupId) || !groupId.IsInt64)
            {
                continue;
            }
            document["SessionKey"] = $"qq/group/{groupId.AsInt64}";
            document.Remove("GroupId");
            await collection.UpdateAsync(document);
        }
    }

    /// <summary>迁移 v3 → v4：移除 MessageKey/DedupKey 字段，统一使用 ObjectId Id。</summary>
    private async Task MigrateMessageKeysV4Async()
    {
        var collection = database.GetCollection("messages");
        var documents = await collection.FindAllAsync();
        foreach (var document in documents)
        {
            bool changed = false;
            if (document.ContainsKey("MessageKey"))
            {
                document.Remove("MessageKey");
                changed = true;
            }
            if (document.ContainsKey("DedupKey"))
            {
                document.Remove("DedupKey");
                changed = true;
            }
            if (changed) await collection.UpdateAsync(document);
        }
    }
}
