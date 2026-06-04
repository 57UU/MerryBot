using CommonLib;
using LiteDB;
using LiteDB.Async;
using System.Security.Cryptography;

namespace DataService;

public class HistoryRecorder : IDisposable
{
    LiteDatabaseAsync database;
    ILiteCollectionAsync<GroupMessage> messagesCollection;
    ILiteCollectionAsync<ImageEntry> imageBedCollection;
    ILiteCollectionAsync<FileEntry> fileBedCollection;
    ILiteCollectionAsync<GroupEvent> eventsCollection;
    ILiteCollectionAsync<ForwardMessageEntry> forwardMessagesCollection;
    ILiteCollectionAsync<GroupNameEntry> groupNameCollection;
    ILiteCollectionAsync<AiMessageEntry> aiMessagesCollection;
    private IdGen.IdGenerator idGenerator;
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

        idGenerator = new(machineCode, IdGenConfig.idGeneratorOptions);

        _ = messagesCollection.EnsureIndexAsync(x => x.GroupId);
        _ = messagesCollection.EnsureIndexAsync(x => x.SenderId);
        _ = messagesCollection.EnsureIndexAsync(x => x.MessageId);
        _ = messagesCollection.EnsureIndexAsync(x => x.Time);
        _ = imageBedCollection.EnsureIndexAsync(x => x.Hash);
        _ = fileBedCollection.EnsureIndexAsync(x => x.Hash);
        _ = eventsCollection.EnsureIndexAsync(x => x.GroupId);
        _ = eventsCollection.EnsureIndexAsync(x => x.EventType);
        _ = eventsCollection.EnsureIndexAsync(x => x.Time);
        _ = forwardMessagesCollection.EnsureIndexAsync(x => x.SourceGroupId);
        _ = groupNameCollection.EnsureIndexAsync(x => x.UpdatedTime);
        _ = aiMessagesCollection.EnsureIndexAsync(x => x.GroupId);
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
        if (await messagesCollection.ExistsAsync(x => x.MessageId == message.MessageId))
        {
            return false;
        }

        await messagesCollection.InsertAsync(message);
        return true;
    }

    public async Task<bool> MessageExistsAsync(long messageId)
    {
        return await messagesCollection.ExistsAsync(x => x.MessageId == messageId);
    }

    public async Task<bool> MarkMessageAsDeletedAsync(long messageId)
    {
        var message = await messagesCollection.FindOneAsync(x => x.MessageId == messageId);
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
        await imageBedCollection.InsertAsync(imageEntry);
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
        await fileBedCollection.InsertAsync(fileEntry);
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

    public async Task<ImageEntry?> GetImageByHashAsync(string hash)
    {
        var cacheKey = $"img_hash_{hash}";
        if (requestCaching.TryGetCache<ImageEntry?>(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }
        var image = await imageBedCollection.FindOneAsync(x => x.Hash == hash);
        requestCaching.SetCache(cacheKey, image);
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
        requestCaching.SetCache(cacheKey, file);
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
        if (await forwardMessagesCollection.ExistsAsync(x => x.ForwardId == forwardEntry.ForwardId))
        {
            return false;
        }

        await forwardMessagesCollection.InsertAsync(forwardEntry);
        return true;
    }

    public async Task<ForwardMessageEntry?> GetForwardMessageByIdAsync(string forwardId)
    {
        return await forwardMessagesCollection.FindOneAsync(x => x.ForwardId == forwardId);
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

    public async Task<bool> RecordAiMessageAsync(long groupId, string messageType, string content)
    {
        var entry = new AiMessageEntry(GenerateId(), groupId, messageType, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await aiMessagesCollection.InsertAsync(entry);
        return true;
    }

    public async Task<List<AiMessageEntry>> GetAiMessagesByGroupIdAsync(long groupId, int page = 1, int pageSize = 50)
    {
        var skip = (page - 1) * pageSize;
        return await aiMessagesCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Id)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetAiMessageCountByGroupIdAsync(long groupId)
    {
        return await aiMessagesCollection.CountAsync(x => x.GroupId == groupId);
    }
}
