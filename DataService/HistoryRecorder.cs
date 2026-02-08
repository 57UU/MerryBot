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
    private IdGen.IdGenerator idGenerator;
    
    public HistoryRecorder(string dbPath,int machineCode=0)
    {
        database = new LiteDatabase(dbPath);
        messagesCollection = database.GetCollection<GroupMessage>("messages");
        imageBedCollection = database.GetCollection<ImageEntry>("images");
        fileBedCollection = database.GetCollection<FileEntry>("files");
        eventsCollection = database.GetCollection<GroupEvent>("events");
        
        idGenerator = new(machineCode, IdGenConfig.idGeneratorOptions);
        
        messagesCollection.EnsureIndex(x => x.GroupId);
        messagesCollection.EnsureIndex(x => x.SenderId);
        messagesCollection.EnsureIndex(x => x.Time);
        imageBedCollection.EnsureIndex(x => x.Hash);
        fileBedCollection.EnsureIndex(x => x.Hash);
        eventsCollection.EnsureIndex(x => x.GroupId);
        eventsCollection.EnsureIndex(x => x.EventType);
        eventsCollection.EnsureIndex(x => x.Time);
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
        
        var updatedMessage = message with { IsDeleted = true };
        messagesCollection.Update(updatedMessage);
        return true;
    }
    
    public List<GroupMessage> GetMessagesByGroupId(long groupId, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupMessage> GetMessagesBySenderId(long senderId, int limit = 100)
    {
        return messagesCollection.Find(x => x.SenderId == senderId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupMessage> GetMessagesByGroupAndSender(long groupId, long senderId, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId && x.SenderId == senderId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupMessage> GetMessagesByTimeRange(DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Find(x => x.Time >= startTime && x.Time <= endTime, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupMessage> GetMessagesByGroupAndTimeRange(long groupId, DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId && x.Time >= startTime && x.Time <= endTime, limit: limit).OrderByDescending(x => x.Time).ToList();
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
    
    public ImageEntry? GetImageByHash(string hash)
    {
        return imageBedCollection.FindOne(x => x.Hash == hash);
    }
    
    public FileEntry? GetFileByHash(string hash)
    {
        return fileBedCollection.FindOne(x => x.Hash == hash);
    }
    
    public bool RecordGroupEvent(GroupEvent groupEvent)
    {
        eventsCollection.Insert(groupEvent);
        return true;
    }
    
    public List<GroupEvent> GetEventsByGroupId(long groupId, int limit = 100)
    {
        return eventsCollection.Find(x => x.GroupId == groupId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupEvent> GetEventsByType(string eventType, int limit = 100)
    {
        return eventsCollection.Find(x => x.EventType == eventType, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<GroupEvent> GetEventsByGroupAndType(long groupId, string eventType, int limit = 100)
    {
        return eventsCollection.Find(x => x.GroupId == groupId && x.EventType == eventType, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    public List<long> GetAllGroupIds()
    {
        var messageGroupIds = messagesCollection.FindAll().Select(x => x.GroupId).Distinct();
        var eventGroupIds = eventsCollection.FindAll().Select(x => x.GroupId).Distinct();
        return messageGroupIds.Concat(eventGroupIds).Distinct().OrderBy(x => x).ToList();
    }
}
