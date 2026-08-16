using CommonLib;
using LiteDB;
using LiteDB.Async;
using System.Security.Cryptography;

namespace DataService;

public class HistoryRecorder : IDisposable
{
    readonly LiteDatabaseAsync database;
    readonly ILiteCollectionAsync<GroupMessage> messagesCollection;
    readonly ILiteCollectionAsync<ImageEntry> imageBedCollection;
    readonly ILiteCollectionAsync<FileEntry> fileBedCollection;
    readonly ILiteCollectionAsync<GroupEvent> eventsCollection;
    readonly ILiteCollectionAsync<ForwardMessageEntry> forwardMessagesCollection;
    readonly ILiteCollectionAsync<GroupNameEntry> groupNameCollection;
    readonly ILiteCollectionAsync<AiMessageEntry> aiMessagesCollection;
    readonly ILiteCollectionAsync<ResourceReference> resourceReferencesCollection;
    private readonly IdGen.IdGenerator idGenerator;
    private readonly string _dbPath;
    private readonly IObjectStorage _objectStorage;
    private const string ImageBucket = "images";
    private const string FileBucket = "files";

    public HistoryRecorder(string dbPath, string storagePath, int machineCode = 0)
    {
        _dbPath = dbPath;
        _objectStorage = new FileSystemObjectStorage(storagePath);
        database = new LiteDatabaseAsync(dbPath);
        messagesCollection = database.GetCollection<GroupMessage>("messages");
        imageBedCollection = database.GetCollection<ImageEntry>("images");
        fileBedCollection = database.GetCollection<FileEntry>("files");
        eventsCollection = database.GetCollection<GroupEvent>("events");
        forwardMessagesCollection = database.GetCollection<ForwardMessageEntry>("forward_messages");
        groupNameCollection = database.GetCollection<GroupNameEntry>("group_names");
        aiMessagesCollection = database.GetCollection<AiMessageEntry>("ai_messages");
        resourceReferencesCollection = database.GetCollection<ResourceReference>("resource_references");

        idGenerator = new(machineCode, IdGenConfig.idGeneratorOptions);

        // 同步等待索引创建完成：避免 fire-and-forget 产生未观察异常，也保证首个查询即命中索引
        EnsureIndexesAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 创建全部索引（含图片/文件 hash 唯一索引）；失败只记日志不抛出，避免历史数据问题导致启动失败。
    /// </summary>
    private async Task EnsureIndexesAsync()
    {
        var tasks = new Task[]
        {
            messagesCollection.EnsureIndexAsync(x => x.GroupId),
            messagesCollection.EnsureIndexAsync(x => x.SenderId),
            messagesCollection.EnsureIndexAsync(x => x.MessageId),
            messagesCollection.EnsureIndexAsync(x => x.Time),
            eventsCollection.EnsureIndexAsync(x => x.GroupId),
            eventsCollection.EnsureIndexAsync(x => x.EventType),
            eventsCollection.EnsureIndexAsync(x => x.Time),
            forwardMessagesCollection.EnsureIndexAsync(x => x.SourceGroupId),
            groupNameCollection.EnsureIndexAsync(x => x.UpdatedTime),
            aiMessagesCollection.EnsureIndexAsync(x => x.SessionKey),
            resourceReferencesCollection.EnsureIndexAsync(x => x.Kind),
        };
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HistoryRecorder] 部分索引创建失败（查询性能可能下降）: {ex.GetBaseException().Message}");
        }

        // hash 唯一索引：已有历史重复数据时创建会失败，只记日志；
        // 唯一索引缺失期间由 RecordImageAsync/RecordFileAsync 的幂等兜底处理并发写入。
        try
        {
            await imageBedCollection.EnsureIndexAsync(x => x.Hash, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HistoryRecorder] images.Hash 唯一索引创建失败（可能存在历史重复数据）: {ex.GetBaseException().Message}");
        }
        try
        {
            await fileBedCollection.EnsureIndexAsync(x => x.Hash, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HistoryRecorder] files.Hash 唯一索引创建失败（可能存在历史重复数据）: {ex.GetBaseException().Message}");
        }
    }

    private long GenerateId()
    {
        return idGenerator.CreateId();
    }
    private static string CalculateHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return ToFileNameSafeBase64String(Convert.ToBase64String(hashBytes));
    }

    internal static string ToFileNameSafeBase64String(string base64)
    {
        return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }
    public void Dispose()
    {
        database.Dispose();
        _objectStorage?.Dispose();
    }

    /// <summary>
    /// 数据库 schema 版本。每次新增迁移步骤时递增。
    /// - 0: 初始版本（未迁移）
    /// - 1: 修正 ForwardData.Content 旧格式（JsonElement? → List&lt;GroupMessage&gt;?）
    /// - 2: 补齐消息业务键并增加本地资源引用集合
    /// - 3: ai_messages 主键从 GroupId 改为 SessionKey（统一会话标识）
    /// </summary>
    private const int CurrentSchemaVersion = 3;

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
        var collection = database.GetCollection("messages");
        var documents = await collection.FindAllAsync();
        foreach (var document in documents)
        {
            if (document.TryGetValue("MessageKey", out var key) && key.IsString && !string.IsNullOrEmpty(key.AsString))
            {
                continue;
            }

            if (!document.TryGetValue("GroupId", out var groupId) || !document.TryGetValue("MessageId", out var messageId))
            {
                continue;
            }

            document["MessageKey"] = GroupMessage.CreateMessageKey(groupId.AsInt64, messageId.AsInt64);
            await collection.UpdateAsync(document);
        }

        await messagesCollection.EnsureIndexAsync(x => x.MessageKey, true);
        await forwardMessagesCollection.EnsureIndexAsync(x => x.ForwardId, true);
        await resourceReferencesCollection.EnsureIndexAsync(x => x.LocalUri, true);
    }

    public async Task<bool> RecordMessageAsync(GroupMessage message)
    {
        return await UpsertMessageAsync(message);
    }

    /// <summary>按群号 + 消息 ID 幂等保存消息；返回 true 表示新增。</summary>
    public async Task<bool> UpsertMessageAsync(GroupMessage message)
    {
        message.MessageKey = GroupMessage.CreateMessageKey(message.GroupId, message.MessageId);
        var existing = await messagesCollection.FindOneAsync(x => x.MessageKey == message.MessageKey);
        if (existing != null)
        {
            message.Id = existing.Id;
            await messagesCollection.UpdateAsync(message);
            return false;
        }

        try
        {
            await messagesCollection.InsertAsync(message);
            return true;
        }
        catch (Exception exception) when (IsLiteDatabaseException(exception))
        {
            // 唯一索引与另一条并发写入竞争时，读取并覆盖现有记录。
            // LiteDB.Async 会将 LiteException 包装成 LiteAsyncException。
            existing = await messagesCollection.FindOneAsync(x => x.MessageKey == message.MessageKey);
            if (existing == null) throw;
            message.Id = existing.Id;
            await messagesCollection.UpdateAsync(message);
            return false;
        }
    }

    public async Task<bool> MessageExistsAsync(long messageId)
    {
        return await messagesCollection.ExistsAsync(x => x.MessageId == messageId);
    }

    /// <summary>
    /// 按消息 ID 与群号查找单条消息（用于读取"回复"引用的原始消息）
    /// </summary>
    public async Task<GroupMessage?> GetMessageByIdAsync(long messageId, long groupId)
    {
        return await messagesCollection.FindOneAsync(x => x.MessageId == messageId && x.GroupId == groupId);
    }

    public async Task<bool> MarkMessageAsDeletedAsync(long messageId, long? groupId = null)
    {
        var message = groupId.HasValue
            ? await messagesCollection.FindOneAsync(x => x.MessageId == messageId && x.GroupId == groupId.Value)
            : await messagesCollection.FindOneAsync(x => x.MessageId == messageId);
        if (message == null)
        {
            return false;
        }

        message.IsDeleted = true;
        return await messagesCollection.UpdateAsync(message);
    }

    public async Task<List<GroupMessage>> GetMessagesByGroupIdAsync(long groupId, int limit = 100)
    {
        return await messagesCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupMessage>> GetMessagesByGroupIdAsync(long groupId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        var skip = (page - 1) * pageSize;
        return await messagesCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<List<GroupMessage>> GetMessagesBySenderIdAsync(long senderId, int limit = 100)
    {
        return await messagesCollection.Query()
            .Where(x => x.SenderId == senderId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupMessage>> GetMessagesByGroupAndSenderAsync(long groupId, long senderId, int limit = 100)
    {
        return await messagesCollection.Query()
            .Where(x => x.GroupId == groupId && x.SenderId == senderId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupMessage>> GetMessagesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100)
    {
        return await messagesCollection.Query()
            .Where(x => x.Time >= startTime && x.Time <= endTime)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupMessage>> GetMessagesByGroupAndTimeRangeAsync(long groupId, DateTime startTime, DateTime endTime, int limit = 100)
    {
        return await messagesCollection.Query()
            .Where(x => x.GroupId == groupId && x.Time >= startTime && x.Time <= endTime)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<ImageEntry> RecordImageAsync(string originalUrl, byte[] data)
    {
        var hash = CalculateHash(data);
        var existingImage = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
        if (existingImage != null)
        {
            return existingImage;
        }

        var id = GenerateId();
        await _objectStorage.StoreAsync(ImageBucket, hash, data);
        var imageEntry = new ImageEntry(id, originalUrl, hash);
        try
        {
            await imageBedCollection.InsertAsync(imageEntry);
        }
        catch (Exception exception) when (IsLiteDatabaseException(exception))
        {
            // 唯一索引竞争：并发写入已插入同 hash 条目；删除刚落盘文件并返回已有条目，避免孤儿文件
            existingImage = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
            if (existingImage != null)
            {
                await _objectStorage.DeleteAsync(ImageBucket, hash);
                return existingImage;
            }
            throw;
        }
        catch
        {
            // 数据库插入失败：删除已落盘文件，保持文件与数据库一致
            try { await _objectStorage.DeleteAsync(ImageBucket, hash); } catch { }
            throw;
        }
        return imageEntry;
    }

    /// <summary>
    /// 把 description 写到 ImageEntry（按 hash 去重的那一层）。幂等。
    /// </summary>
    public async Task SetImageEntryDescriptionAsync(string hash, string description)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(description)) return;
        var entry = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
        if (entry == null || entry.Description == description) return;
        entry.Description = description;
        await imageBedCollection.UpdateAsync(entry);
    }

    public async Task<FileEntry> RecordFileAsync(string originalUrl, byte[] data)
    {
        var hash = CalculateHash(data);
        var existingFile = await fileBedCollection.FindOneAsync(x => x.Hash == hash);
        if (existingFile != null)
        {
            return existingFile;
        }

        var id = GenerateId();
        await _objectStorage.StoreAsync(FileBucket, hash, data);
        var fileEntry = new FileEntry(id, originalUrl, hash);
        try
        {
            await fileBedCollection.InsertAsync(fileEntry);
        }
        catch (Exception exception) when (IsLiteDatabaseException(exception))
        {
            // 唯一索引竞争：并发写入已插入同 hash 条目；删除刚落盘文件并返回已有条目，避免孤儿文件
            existingFile = await fileBedCollection.FindOneAsync(x => x.Hash == hash);
            if (existingFile != null)
            {
                await _objectStorage.DeleteAsync(FileBucket, hash);
                return existingFile;
            }
            throw;
        }
        catch
        {
            // 数据库插入失败：删除已落盘文件，保持文件与数据库一致
            try { await _objectStorage.DeleteAsync(FileBucket, hash); } catch { }
            throw;
        }
        return fileEntry;
    }

    public async Task<ImageEntry?> GetImageByIdAsync(long id)
    {
        return await imageBedCollection.FindOneAsync(x => x.Id == id);
    }

    public async Task<FileEntry?> GetFileByIdAsync(long id)
    {
        return await fileBedCollection.FindOneAsync(x => x.Id == id);
    }

    public async Task<byte[]?> GetImageDataAsync(string hash)
    {
        return await _objectStorage.GetAsync(ImageBucket, hash);
    }

    public async Task<byte[]?> GetFileDataAsync(string hash)
    {
        return await _objectStorage.GetAsync(FileBucket, hash);
    }

    private readonly RequestCaching requestCaching = new(TimeSpan.FromHours(24));
    // 命中与未命中分开过期：查无结果 1 小时后重试，避免长时间缓存“不存在”
    private static readonly TimeSpan CacheHitExpiration = TimeSpan.FromHours(24);
    private static readonly TimeSpan CacheMissExpiration = TimeSpan.FromHours(1);

    public async Task<ImageEntry?> GetImageByHashAsync(string hash)
    {
        var cacheKey = $"img_hash_{hash}";
        if (requestCaching.TryGetCache<ImageEntry?>(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }
        var image = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
        requestCaching.SetCache(cacheKey, image, image == null ? CacheMissExpiration : CacheHitExpiration);
        return image;
    }

    public async Task<FileEntry?> GetFileByHashAsync(string hash)
    {
        var cacheKey = $"file_hash_{hash}";
        if (requestCaching.TryGetCache<FileEntry?>(cacheKey, out var cachedFile))
        {
            return cachedFile;
        }
        var file = await fileBedCollection.FindOneAsync(x => x.Hash == hash);
        requestCaching.SetCache(cacheKey, file, file == null ? CacheMissExpiration : CacheHitExpiration);
        return file;
    }

    public async Task<bool> RecordGroupEventAsync(GroupEvent groupEvent)
    {
        await eventsCollection.InsertAsync(groupEvent);
        return true;
    }

    public async Task<List<GroupEvent>> GetEventsByGroupIdAsync(long groupId, int limit = 100)
    {
        return await eventsCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupEvent>> GetEventsByTypeAsync(string eventType, int limit = 100)
    {
        return await eventsCollection.Query()
            .Where(x => x.EventType == eventType)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<GroupEvent>> GetEventsByGroupAndTypeAsync(long groupId, string eventType, int limit = 100)
    {
        return await eventsCollection.Query()
            .Where(x => x.GroupId == groupId && x.EventType == eventType)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<long>> GetAllGroupIdsAsync()
    {
        var messages = await messagesCollection.FindAllAsync();
        var events = await eventsCollection.FindAllAsync();
        var messageGroupIds = messages.Select(x => x.GroupId).Distinct();
        var eventGroupIds = events.Select(x => x.GroupId).Distinct();
        return messageGroupIds.Concat(eventGroupIds).Distinct().OrderBy(x => x).ToList();
    }

    public async Task<bool> RecordForwardMessageAsync(ForwardMessageEntry forwardEntry)
    {
        var existing = await forwardMessagesCollection.FindOneAsync(x => x.ForwardId == forwardEntry.ForwardId);
        if (existing != null)
        {
            forwardEntry.Id = existing.Id;
            await forwardMessagesCollection.UpdateAsync(forwardEntry);
            return false;
        }
        try
        {
            await forwardMessagesCollection.InsertAsync(forwardEntry);
            return true;
        }
        catch (Exception exception) when (IsLiteDatabaseException(exception))
        {
            existing = await forwardMessagesCollection.FindOneAsync(x => x.ForwardId == forwardEntry.ForwardId);
            if (existing == null) throw;
            forwardEntry.Id = existing.Id;
            await forwardMessagesCollection.UpdateAsync(forwardEntry);
            return false;
        }
    }

    public async Task<ResourceReference?> GetResourceReferenceAsync(string localUri)
        => await resourceReferencesCollection.FindOneAsync(x => x.LocalUri == localUri);

    public async Task<ResourceReference> UpsertResourceReferenceAsync(ResourceReference reference)
    {
        var existing = await resourceReferencesCollection.FindOneAsync(x => x.LocalUri == reference.LocalUri);
        if (existing == null)
        {
            try
            {
                await resourceReferencesCollection.InsertAsync(reference);
                return reference;
            }
            catch (Exception exception) when (IsLiteDatabaseException(exception))
            {
                existing = await resourceReferencesCollection.FindOneAsync(x => x.LocalUri == reference.LocalUri);
                if (existing == null) throw;
            }
        }

        existing.Kind = reference.Kind;
        existing.Source = reference.Source;
        existing.OriginalName ??= reference.OriginalName;
        existing.StoredObjectId = reference.StoredObjectId ?? existing.StoredObjectId;
        existing.IsImage = reference.IsImage;
        existing.UpdatedTime = reference.UpdatedTime;
        await resourceReferencesCollection.UpdateAsync(existing);
        return existing;
    }

    /// <summary>
    /// LiteDB.Async 将底层 LiteDB 异常包在 <see cref="LiteAsyncException"/> 中；
    /// 并发插入时两种异常都应走“重新读取后更新”的幂等分支。
    /// </summary>
    private static bool IsLiteDatabaseException(Exception exception) =>
        exception is LiteException ||
        exception is LiteAsyncException { InnerException: LiteException };

    public async Task<ForwardMessageEntry?> GetForwardMessageByIdAsync(string forwardId)
    {
        forwardId = NormalizeForwardId(forwardId);
        return await forwardMessagesCollection.FindOneAsync(x => x.ForwardId == forwardId);
    }

    private static string NormalizeForwardId(string forwardId)
    {
        const string prefix = "merrybot://forward/";
        if (!forwardId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return forwardId;
        var encoded = forwardId[prefix.Length..];
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)));
        }
        catch (FormatException)
        {
            return forwardId;
        }
    }

    public async Task<bool> ForwardMessageExistsAsync(string forwardId)
    {
        return await forwardMessagesCollection.ExistsAsync(x => x.ForwardId == forwardId);
    }

    public async Task<bool> RecordOrUpdateGroupNameAsync(GroupNameEntry groupNameEntry)
    {
        var existingEntry = await groupNameCollection.FindOneAsync(x => x.GroupId == groupNameEntry.GroupId);
        if (existingEntry != null)
        {
            existingEntry.Name = groupNameEntry.Name;
            existingEntry.MemberCount = groupNameEntry.MemberCount;
            existingEntry.MaxMemberCount = groupNameEntry.MaxMemberCount;
            existingEntry.UpdatedTime = groupNameEntry.UpdatedTime;
            return await groupNameCollection.UpdateAsync(existingEntry);
        }
        else
        {
            await groupNameCollection.InsertAsync(groupNameEntry);
            return true;
        }
    }

    public async Task<GroupNameEntry?> GetGroupNameByIdAsync(long groupId)
    {
        return await groupNameCollection.FindOneAsync(x => x.GroupId == groupId);
    }

    public async Task<List<GroupNameEntry>> GetAllGroupNamesAsync()
    {
        return (await groupNameCollection.FindAllAsync()).ToList();
    }

    public async Task<bool> DeleteGroupNameAsync(long groupId)
    {
        return await groupNameCollection.DeleteAsync(groupId);
    }

    public async Task<int> GetImageCountAsync()
    {
        return await imageBedCollection.CountAsync();
    }

    public async Task<int> GetFileCountAsync()
    {
        return await fileBedCollection.CountAsync();
    }

    public string GetDatabaseSize()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                var fileInfo = new FileInfo(_dbPath);
                return Format.FormatFileSize(fileInfo.Length);
            }
            return "0 B";
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task<string> GetObjectStorageSizeAsync()
    {
        try
        {
            var imageSize = await _objectStorage.GetTotalSizeAsync(ImageBucket);
            var fileSize = await _objectStorage.GetTotalSizeAsync(FileBucket);
            var totalSize = imageSize + fileSize;
            return Format.FormatFileSize(totalSize);
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task<string> GetImageStorageSizeAsync()
    {
        try
        {
            var imageSize = await _objectStorage.GetTotalSizeAsync(ImageBucket);
            return Format.FormatFileSize(imageSize);
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task<string> GetFileStorageSizeAsync()
    {
        try
        {
            var fileSize = await _objectStorage.GetTotalSizeAsync(FileBucket);
            return Format.FormatFileSize(fileSize);
        }
        catch
        {
            return "Unknown";
        }
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

    public async Task<bool> RecordAiMessageAsync(string sessionKey, string messageType, string content, int inputTokens = 0, int outputTokens = 0, int totalTokens = 0)
    {
        var entry = new AiMessageEntry(GenerateId(), sessionKey, messageType, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), inputTokens, outputTokens, totalTokens);
        await aiMessagesCollection.InsertAsync(entry);
        return true;
    }

    public async Task<List<AiMessageEntry>> GetAiMessagesBySessionKeyAsync(string sessionKey, int page = 1, int pageSize = 50)
    {
        page = Math.Max(1, page);
        var skip = (page - 1) * pageSize;
        return await aiMessagesCollection.Query()
            .Where(x => x.SessionKey == sessionKey)
            .OrderByDescending(x => x.Id)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetAiMessageCountBySessionKeyAsync(string sessionKey)
    {
        return await aiMessagesCollection.CountAsync(x => x.SessionKey == sessionKey);
    }

    public async Task<int> GetMessageCountByGroupIdAsync(long groupId)
    {
        return await messagesCollection.CountAsync(x => x.GroupId == groupId);
    }
}
