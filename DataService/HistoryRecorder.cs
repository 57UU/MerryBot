using CommonLib;
using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DataService;

public class HistoryRecorder : IDisposable
{
    LiteDatabase database;
    ILiteCollection<GroupMessage> messagesCollection;
    ILiteCollection<ImageEntry> imageBedCollection;
    ILiteCollection<FileEntry> fileBedCollection;
    ILiteCollection<GroupEvent> eventsCollection;
    ILiteCollection<ForwardMessageEntry> forwardMessagesCollection;
    ILiteCollection<GroupNameEntry> groupNameCollection;
    private IdGen.IdGenerator idGenerator;
    private readonly string _dbPath;
    
    public HistoryRecorder(string dbPath,int machineCode=0)
    {
        _dbPath = dbPath;
        database = new LiteDatabase(dbPath);
        messagesCollection = database.GetCollection<GroupMessage>("messages");
        imageBedCollection = database.GetCollection<ImageEntry>("images");
        fileBedCollection = database.GetCollection<FileEntry>("files");
        eventsCollection = database.GetCollection<GroupEvent>("events");
        forwardMessagesCollection = database.GetCollection<ForwardMessageEntry>("forward_messages");
        groupNameCollection = database.GetCollection<GroupNameEntry>("group_names");
        
        idGenerator = new(machineCode, IdGenConfig.idGeneratorOptions);
        
        messagesCollection.EnsureIndex(x => x.GroupId);
        messagesCollection.EnsureIndex(x => x.SenderId);
        messagesCollection.EnsureIndex(x => x.MessageId);
        messagesCollection.EnsureIndex(x => x.Time);
        imageBedCollection.EnsureIndex(x => x.Hash);
        fileBedCollection.EnsureIndex(x => x.Hash);
        eventsCollection.EnsureIndex(x => x.GroupId);
        eventsCollection.EnsureIndex(x => x.EventType);
        eventsCollection.EnsureIndex(x => x.Time);
        forwardMessagesCollection.EnsureIndex(x => x.SourceGroupId);
        groupNameCollection.EnsureIndex(x => x.UpdatedTime);
    }
    
    private long GenerateId()
    {
        return idGenerator.CreateId();
    }
    
    private string CalculateHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToBase64String(hashBytes);
    }
   
    public void Dispose()
    {
        database.Dispose();
    }
    
    public bool RecordMessage(GroupMessage message)
    {
        if (messagesCollection.Exists(x => x.MessageId == message.MessageId))
        {
            return false;
        }
        
        messagesCollection.Insert(message);
        return true;
    }
    
    public bool MessageExists(long messageId)
    {
        return messagesCollection.Exists(x => x.MessageId == messageId);
    }
    
    public bool MarkMessageAsDeleted(long messageId)
    {
        var message = messagesCollection.FindOne(x => x.MessageId == messageId);
        if (message == null)
        {
            return false;
        }
        
        message.IsDeleted = true;
        messagesCollection.Update(message);
        return true;
    }
    
    public List<GroupMessage> GetMessagesByGroupId(long groupId, int limit = 100)
    {
        return messagesCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupMessage> GetMessagesByGroupId(long groupId, int page, int pageSize)
    {
        var skip = (page - 1) * pageSize;
        return messagesCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Skip(skip)
            .Limit(pageSize)
            .ToList();
    }
    
    public List<GroupMessage> GetMessagesBySenderId(long senderId, int limit = 100)
    {
        return messagesCollection.Query()
            .Where(x => x.SenderId == senderId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupMessage> GetMessagesByGroupAndSender(long groupId, long senderId, int limit = 100)
    {
        return messagesCollection.Query()
            .Where(x => x.GroupId == groupId && x.SenderId == senderId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupMessage> GetMessagesByTimeRange(DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Query()
            .Where(x => x.Time >= startTime && x.Time <= endTime)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupMessage> GetMessagesByGroupAndTimeRange(long groupId, DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Query()
            .Where(x => x.GroupId == groupId && x.Time >= startTime && x.Time <= endTime)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public ImageEntry RecordImage(string originalUrl, byte[] data)
    {
        var hash = CalculateHash(data);
        var existingImage = imageBedCollection.FindOne(x => x.Hash == hash);
        if (existingImage != null)
        {
            return existingImage;
        }
        
        var id = GenerateId();
        var imageEntry = new ImageEntry(id, originalUrl, hash, data);
        imageBedCollection.Insert(imageEntry);
        return imageEntry;
    }
    
    public FileEntry RecordFile(string originalUrl, byte[] data)
    {
        var hash = CalculateHash(data);
        var existingFile = fileBedCollection.FindOne(x => x.Hash == hash);
        if (existingFile != null)
        {
            return existingFile;
        }
        
        var id = GenerateId();
        var fileEntry = new FileEntry(id, originalUrl, hash, data);
        fileBedCollection.Insert(fileEntry);
        return fileEntry;
    }
    
    public ImageEntry? GetImageById(long id)
    {
        return imageBedCollection.FindOne(x => x.Id == id);
    }
    
    public FileEntry? GetFileById(long id)
    {
        return fileBedCollection.FindOne(x => x.Id == id);
    }
    private readonly RequestCaching requestCaching = new(TimeSpan.FromHours(24));
    public ImageEntry? GetImageByHash(string hash)
    {
        var cacheKey = $"img_hash_{hash}";
        if (requestCaching.TryGetCache<ImageEntry?>(cacheKey, out var cachedImage))
        {
            return cachedImage;
        }
        var image = imageBedCollection.FindOne(x => x.Hash == hash);
        requestCaching.SetCache(cacheKey, image);
        return image;
    }

    public FileEntry? GetFileByHash(string hash)
    {
        var cacheKey = $"file_hash_{hash}";
        if (requestCaching.TryGetCache<FileEntry?>(cacheKey, out var cachedFile))
        {
            return cachedFile;
        }
        var file = fileBedCollection.FindOne(x => x.Hash == hash);
        requestCaching.SetCache(cacheKey, file);
        return file;
    }
    
    public bool RecordGroupEvent(GroupEvent groupEvent)
    {
        eventsCollection.Insert(groupEvent);
        return true;
    }
    
    public List<GroupEvent> GetEventsByGroupId(long groupId, int limit = 100)
    {
        return eventsCollection.Query()
            .Where(x => x.GroupId == groupId)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupEvent> GetEventsByType(string eventType, int limit = 100)
    {
        return eventsCollection.Query()
            .Where(x => x.EventType == eventType)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<GroupEvent> GetEventsByGroupAndType(long groupId, string eventType, int limit = 100)
    {
        return eventsCollection.Query()
            .Where(x => x.GroupId == groupId && x.EventType == eventType)
            .OrderByDescending(x => x.Time)
            .Limit(limit)
            .ToList();
    }
    
    public List<long> GetAllGroupIds()
    {
        var messageGroupIds = messagesCollection.FindAll().Select(x => x.GroupId).Distinct();
        var eventGroupIds = eventsCollection.FindAll().Select(x => x.GroupId).Distinct();
        return messageGroupIds.Concat(eventGroupIds).Distinct().OrderBy(x => x).ToList();
    }
    
    public bool RecordForwardMessage(ForwardMessageEntry forwardEntry)
    {
        if (forwardMessagesCollection.Exists(x => x.ForwardId == forwardEntry.ForwardId))
        {
            return false;
        }
        
        forwardMessagesCollection.Insert(forwardEntry);
        return true;
    }
    
    public ForwardMessageEntry? GetForwardMessageById(string forwardId)
    {
        return forwardMessagesCollection.FindOne(x => x.ForwardId == forwardId);
    }
    
    public bool ForwardMessageExists(string forwardId)
    {
        return forwardMessagesCollection.Exists(x => x.ForwardId == forwardId);
    }
    
    public bool RecordOrUpdateGroupName(GroupNameEntry groupNameEntry)
    {
        var existingEntry = groupNameCollection.FindOne(x => x.GroupId == groupNameEntry.GroupId);
        if (existingEntry != null)
        {
            existingEntry.Name = groupNameEntry.Name;
            existingEntry.MemberCount = groupNameEntry.MemberCount;
            existingEntry.MaxMemberCount = groupNameEntry.MaxMemberCount;
            existingEntry.UpdatedTime = groupNameEntry.UpdatedTime;
            return groupNameCollection.Update(existingEntry);
        }
        else
        {
            groupNameCollection.Insert(groupNameEntry);
            return true;
        }
    }
    
    public GroupNameEntry? GetGroupNameById(long groupId)
    {
        return groupNameCollection.FindOne(x => x.GroupId == groupId);
    }
    
    public List<GroupNameEntry> GetAllGroupNames()
    {
        return groupNameCollection.FindAll().ToList();
    }
    
    public bool DeleteGroupName(long groupId)
    {
        return groupNameCollection.Delete(groupId);
    }
    
    public int GetImageCount()
    {
        return imageBedCollection.Count();
    }
    
    public int GetFileCount()
    {
        return fileBedCollection.Count();
    }
    
    public string GetDatabaseSize()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                var fileInfo = new FileInfo(_dbPath);
                return FormatFileSize(fileInfo.Length);
            }
            return "0 B";
        }
        catch
        {
            return "Unknown";
        }
    }
    
    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
