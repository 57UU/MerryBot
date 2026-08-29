using CommonLib;
using LiteDB;
using LiteDB.Async;
using System.Security.Cryptography;

namespace DataService;

public partial class HistoryRecorder : IDisposable
{
    readonly LiteDatabaseAsync database;
    readonly ILiteCollectionAsync<GroupMessage> messagesCollection;
    readonly ILiteCollectionAsync<ImageEntry> imageBedCollection;
    readonly ILiteCollectionAsync<FileEntry> fileBedCollection;
    readonly ILiteCollectionAsync<GroupEvent> eventsCollection;
    readonly ILiteCollectionAsync<ForwardMessageEntry> forwardMessagesCollection;
    readonly ILiteCollectionAsync<GroupNameEntry> groupNameCollection;
    readonly ILiteCollectionAsync<ResourceReference> resourceReferencesCollection;
    private readonly IdGen.IdGenerator idGenerator;
    private readonly string _dbPath;
    private readonly IObjectStorage _objectStorage;
    private readonly ISimpleLogger _logger;
    private const string ImageBucket = "images";
    private const string FileBucket = "files";

    public HistoryRecorder(string dbPath, string storagePath, int machineCode = 0, ISimpleLogger? logger = null)
    {
        _dbPath = dbPath;
        _objectStorage = new FileSystemObjectStorage(storagePath);
        _logger = logger ?? SimpleLog.Default;
        database = new LiteDatabaseAsync(dbPath);
        // 读取时直接返回 UTC，避免 LiteDB 按机器本地时区转换（UTC_DATE pragma）。底层存储始终是 UTC。
        database.UtcDate = true;
        messagesCollection = database.GetCollection<GroupMessage>("messages");
        imageBedCollection = database.GetCollection<ImageEntry>("images");
        fileBedCollection = database.GetCollection<FileEntry>("files");
        eventsCollection = database.GetCollection<GroupEvent>("events");
        forwardMessagesCollection = database.GetCollection<ForwardMessageEntry>("forward_messages");
        groupNameCollection = database.GetCollection<GroupNameEntry>("group_names");
        resourceReferencesCollection = database.GetCollection<ResourceReference>("resource_references");

        idGenerator = new(machineCode, IdGenConfig.idGeneratorOptions);

        // 同步等待索引创建完成：避免 fire-and-forget 产生未观察异常，也保证首个查询即命中索引
        EnsureIndexesAsync().GetAwaiter().GetResult();
        // AI 消息存储共享同一数据库连接，由本类组合并负责其生命周期
        AiMessages = new AiMessageStore(database, idGenerator, _logger);
    }

    /// <summary>AI 消息审计存储（ai_messages 集合），与群消息历史同库。</summary>
    public AiMessageStore AiMessages { get; }

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
            resourceReferencesCollection.EnsureIndexAsync(x => x.Kind),
        };
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[HistoryRecorder] 部分索引创建失败（查询性能可能下降）: {ex.GetBaseException().Message}");
        }

        // hash 唯一索引：已有历史重复数据时创建会失败，只记日志；
        // 唯一索引缺失期间由 RecordImageAsync/RecordFileAsync 的幂等兜底处理并发写入。
        try
        {
            await imageBedCollection.EnsureIndexAsync(x => x.Hash, true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[HistoryRecorder] images.Hash 唯一索引创建失败（可能存在历史重复数据）: {ex.GetBaseException().Message}");
        }
        try
        {
            await fileBedCollection.EnsureIndexAsync(x => x.Hash, true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[HistoryRecorder] files.Hash 唯一索引创建失败（可能存在历史重复数据）: {ex.GetBaseException().Message}");
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

    public async Task<bool> RecordMessageAsync(GroupMessage message)
    {
        return await UpsertMessageAsync(message);
    }

    public async Task<bool> UpsertMessageAsync(GroupMessage message)
    {
        var dedup = await messagesCollection.FindOneAsync(x => x.GroupId == message.GroupId && x.MessageId == message.MessageId && x.Time == message.Time);
        if (dedup != null)
        {
            message.Id = dedup.Id;
            await messagesCollection.UpdateAsync(message);
            return false;
        }
        if (message.Id == ObjectId.Empty) message.Id = ObjectId.NewObjectId();

        try
        {
            await messagesCollection.InsertAsync(message);
            return true;
        }
        catch (Exception exception) when (IsLiteDatabaseException(exception))
        {
            dedup = await messagesCollection.FindOneAsync(x => x.GroupId == message.GroupId && x.MessageId == message.MessageId && x.Time == message.Time);
            if (dedup != null)
            {
                message.Id = dedup.Id;
                await messagesCollection.UpdateAsync(message);
                return false;
            }
            throw;
        }
    }

    public async Task<GroupMessage?> GetMessageByObjectIdAsync(string objectIdHex)
    {
        if (string.IsNullOrWhiteSpace(objectIdHex) || !TryParseObjectId(objectIdHex, out var oid)) return null;
        return await messagesCollection.FindOneAsync(x => x.Id == oid);
    }

    public async Task<bool> MessageExistsAsync(long messageId)
    {
        return await messagesCollection.ExistsAsync(x => x.MessageId == messageId);
    }

    public async Task<bool> MessageExistsAsync(long messageId, long groupId)
    {
        return await messagesCollection.ExistsAsync(x => x.MessageId == messageId && x.GroupId == groupId);
    }

    /// <summary>
    /// 按消息 ID 与群号查找单条消息（用于读取"回复"引用的原始消息）。回绕时同 Id 多条，取 Time 最新。
    /// </summary>
    public async Task<GroupMessage?> GetMessageByIdAsync(long messageId, long groupId)
    {
        var list = await messagesCollection.Query().Where(x => x.MessageId == messageId && x.GroupId == groupId).OrderByDescending(x => x.Time).Limit(1).ToListAsync();
        return list.FirstOrDefault();
    }

    public async Task<GroupMessage?> GetMessageByKeyOrIdAsync(string keyOrId, long groupId)
    {
        if (TryParseObjectId(keyOrId, out _))
        {
            var byKey = await GetMessageByObjectIdAsync(keyOrId);
            if (byKey != null) return byKey;
        }
        if (long.TryParse(keyOrId, out var mid)) return await GetMessageByIdAsync(mid, groupId);
        return null;
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

    [Obsolete("Skip 分页深度变慢，请改用 GetMessagesByGroupIdBeforeAsync 游标分页")]
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

    /// <summary>
    /// 游标分页：按 Time 倒序，锚点为 messageId（随机）时先查其 Time 再按 Time 翻页，保证时间顺序。
    /// beforeMessageId == null 返回最新；否则返回同群且 Time 更早（同秒则 MessageId 更小，避免丢同秒消息）的前一页。O(limit)。
    /// </summary>
    public async Task<List<GroupMessage>> GetMessagesByGroupIdBeforeAsync(long groupId, long? beforeMessageId, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        var baseQuery = messagesCollection.Query().Where(x => x.GroupId == groupId);

        if (!beforeMessageId.HasValue)
        {
            var first = await baseQuery.OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
            first.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
            return first;
        }

        var anchorId = beforeMessageId.Value;
        var anchor = await messagesCollection.FindOneAsync(x => x.GroupId == groupId && x.MessageId == anchorId);
        if (anchor == null)
        {
            var fallback = await baseQuery.Where(x => x.MessageId < anchorId).OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
            fallback.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
            return fallback;
        }

        var anchorTime = anchor.Time;
        var list = await baseQuery.Where(x => x.Time < anchorTime || (x.Time == anchorTime && x.MessageId < anchorId))
            .OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
        list.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
        return list;
    }

    public async Task<List<GroupMessage>> GetMessagesByGroupIdBeforeKeyAsync(long groupId, string? beforeMessageKey, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        var baseQuery = messagesCollection.Query().Where(x => x.GroupId == groupId);
        if (string.IsNullOrWhiteSpace(beforeMessageKey) || !TryParseObjectId(beforeMessageKey, out var oid))
        {
            var first = await baseQuery.OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
            first.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
            return first;
        }
        var anchor = await messagesCollection.FindOneAsync(x => x.Id == oid);
        if (anchor == null)
        {
            var first = await baseQuery.OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
            first.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
            return first;
        }
        var anchorTime = anchor.Time;
        var anchorId = anchor.MessageId;
        var list = await baseQuery.Where(x => x.Time < anchorTime || (x.Time == anchorTime && x.MessageId < anchorId))
            .OrderByDescending(x => x.Time).Limit(limit).ToListAsync();
        list.Sort((a, b) => { var c = b.Time.CompareTo(a.Time); return c != 0 ? c : b.MessageId.CompareTo(a.MessageId); });
        return list;
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

    public async Task<ImageEntry> RecordImageAsync(byte[] data, string? fileType = null)
    {
        var hash = CalculateHash(data);
        var existingImage = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
        if (existingImage != null)
        {
            // 已有记录但 FileType 为空时，补上扩展名
            if (string.IsNullOrEmpty(existingImage.FileType) && !string.IsNullOrWhiteSpace(fileType))
            {
                existingImage.FileType = fileType;
                try { await imageBedCollection.UpdateAsync(existingImage); } catch { }
            }
            return existingImage;
        }

        var id = GenerateId();
        await _objectStorage.StoreAsync(ImageBucket, hash, data);
        var imageEntry = new ImageEntry(id, hash, fileType ?? string.Empty);
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

    public async Task<FileEntry> RecordFileAsync(byte[] data, string? fileType = null)
    {
        var hash = CalculateHash(data);
        var existingFile = await fileBedCollection.FindOneAsync(x => x.Hash == hash);
        if (existingFile != null)
        {
            if (string.IsNullOrEmpty(existingFile.FileType) && !string.IsNullOrWhiteSpace(fileType))
            {
                existingFile.FileType = fileType;
                try { await fileBedCollection.UpdateAsync(existingFile); } catch { }
            }
            return existingFile;
        }

        var id = GenerateId();
        await _objectStorage.StoreAsync(FileBucket, hash, data);
        var fileEntry = new FileEntry(id, hash, fileType ?? string.Empty);
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

    private static bool TryParseObjectId(string s, out ObjectId result)
    {
        try { result = new ObjectId(s); return true; }
        catch { result = ObjectId.Empty; return false; }
    }

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

    public long GetDatabaseFileSize()
    {
        try
        {
            return File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public string GetDatabaseSize() => Format.FormatFileSize(GetDatabaseFileSize());

    /// <summary>
    /// 执行 LiteDB Rebuild（碎片整理/压缩）：重写整个数据库文件，回收空洞并重建索引。
    /// 需独占数据库，执行期间会阻塞其他读写；返回减少的字节数（before - after）。
    /// 索引损坏（如 Detected loop）时改用容错模式（IncludeErrorReport）并记录警告，避免直接 500。
    /// </summary>
    public async Task<long> RebuildAsync()
    {
        long before = GetDatabaseFileSize();
        try
        {
            await database.RebuildAsync(new LiteDB.Engine.RebuildOptions());
        }
        catch (Exception ex) when (IsLoopException(ex))
        {
            _logger.Warn($"[HistoryRecorder] 检测到索引损坏（{ex.GetBaseException().Message}），尝试容错 Rebuild（IncludeErrorReport=true）...");
            var opts = new LiteDB.Engine.RebuildOptions { IncludeErrorReport = true };
            await database.RebuildAsync(opts);
            var errors = opts.GetErrorReport().ToList();
            if (errors.Count > 0)
            {
                _logger.Warn($"[HistoryRecorder] 容错 Rebuild 完成，但有 {errors.Count} 条错误被跳过（部分数据可能丢失），首条: {errors[0]}");
            }
            else
            {
                _logger.Warn("[HistoryRecorder] 容错 Rebuild 完成，未报告错误。");
            }
        }
        long after = GetDatabaseFileSize();
        return before - after;
    }

    private static bool IsLoopException(Exception ex)
    {
        var msg = ex.GetBaseException().Message ?? "";
        return msg.Contains("loop", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Detected loop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>尝试执行 Checkpoint（截断 WAL/journal），不重建索引，可在 Rebuild 失败时回收部分空间。</summary>
    public async Task<long> CheckpointAsync()
    {
        long before = GetDatabaseFileSize();
        try
        {
            // LiteDatabaseAsync 未直接暴露 Checkpoint，通过底层 LiteDatabase 调用
            // 使用同步 API 包装为 Task，避免阻塞
            await database.CheckpointAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[HistoryRecorder] Checkpoint 失败: {ex.GetBaseException().Message}");
            throw;
        }
        long after = GetDatabaseFileSize();
        return before - after;
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

    public async Task<int> GetMessageCountByGroupIdAsync(long groupId)
    {
        return await messagesCollection.CountAsync(x => x.GroupId == groupId);
    }
}
