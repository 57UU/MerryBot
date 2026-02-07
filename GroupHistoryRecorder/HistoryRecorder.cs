using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GroupHistoryRecorder;

public class HistoryRecorder : IDisposable
{
    LiteDatabase database;
    ILiteCollection<GroupMessage> messagesCollection;
    ILiteCollection<ImageEntry> imageBedCollection;
    ILiteCollection<FileEntry> fileBedCollection;
    
    public HistoryRecorder(string dbPath)
    {
        database = new LiteDatabase(dbPath);
        messagesCollection = database.GetCollection<GroupMessage>("messages");
        imageBedCollection = database.GetCollection<ImageEntry>("images");
        fileBedCollection = database.GetCollection<FileEntry>("files");
        
        // 创建索引以提高查询性能
        messagesCollection.EnsureIndex(x => x.GroupId);
        messagesCollection.EnsureIndex(x => x.SenderId);
        messagesCollection.EnsureIndex(x => x.Time);
    }
   
    public void Dispose()
    {
        database.Dispose();
    }
    
    /// <summary>
    /// 记录群聊消息
    /// </summary>
    /// <param name="message">要记录的群聊消息</param>
    /// <returns>是否成功记录（如果消息已存在则返回 false）</returns>
    public bool RecordMessage(GroupMessage message)
    {
        // 检查消息是否已存在
        if (messagesCollection.Exists(x => x.MessageId == message.MessageId))
        {
            return false;
        }
        
        messagesCollection.Insert(message);
        return true;
    }
    
    /// <summary>
    /// 检查消息是否存在
    /// </summary>
    /// <param name="messageId">消息ID</param>
    /// <returns>消息是否存在</returns>
    public bool MessageExists(long messageId)
    {
        return messagesCollection.Exists(x => x.MessageId == messageId);
    }
    
    /// <summary>
    /// 根据群ID获取消息
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="limit">返回消息数量限制</param>
    /// <returns>消息列表</returns>
    public List<GroupMessage> GetMessagesByGroupId(long groupId, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    /// <summary>
    /// 根据发送者ID获取消息
    /// </summary>
    /// <param name="senderId">发送者ID</param>
    /// <param name="limit">返回消息数量限制</param>
    /// <returns>消息列表</returns>
    public List<GroupMessage> GetMessagesBySenderId(long senderId, int limit = 100)
    {
        return messagesCollection.Find(x => x.SenderId == senderId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    /// <summary>
    /// 获取指定群和发送者的消息
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="senderId">发送者ID</param>
    /// <param name="limit">返回消息数量限制</param>
    /// <returns>消息列表</returns>
    public List<GroupMessage> GetMessagesByGroupAndSender(long groupId, long senderId, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId && x.SenderId == senderId, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    /// <summary>
    /// 根据时间范围获取消息
    /// </summary>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <param name="limit">返回消息数量限制</param>
    /// <returns>消息列表</returns>
    public List<GroupMessage> GetMessagesByTimeRange(DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Find(x => x.Time >= startTime && x.Time <= endTime, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    /// <summary>
    /// 获取指定群的时间范围内的消息
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <param name="limit">返回消息数量限制</param>
    /// <returns>消息列表</returns>
    public List<GroupMessage> GetMessagesByGroupAndTimeRange(long groupId, DateTime startTime, DateTime endTime, int limit = 100)
    {
        return messagesCollection.Find(x => x.GroupId == groupId && x.Time >= startTime && x.Time <= endTime, limit: limit).OrderByDescending(x => x.Time).ToList();
    }
    
    /// <summary>
    /// 记录图片到图片床
    /// </summary>
    /// <param name="imageEntry">图片数据</param>
    public void RecordImage(ImageEntry imageEntry)
    {
        imageBedCollection.Insert(imageEntry);
    }
    
    /// <summary>
    /// 记录文件到文件床
    /// </summary>
    /// <param name="fileEntry">文件数据</param>
    public void RecordFile(FileEntry fileEntry)
    {
        fileBedCollection.Insert(fileEntry);
    }
    
    /// <summary>
    /// 根据URL获取图片
    /// </summary>
    /// <param name="url">图片URL</param>
    /// <returns>图片数据</returns>
    public ImageEntry? GetImageByUrl(string url)
    {
        return imageBedCollection.FindOne(x => x.Url == url);
    }
    
    /// <summary>
    /// 根据URL获取文件
    /// </summary>
    /// <param name="url">文件URL</param>
    /// <returns>文件数据</returns>
    public FileEntry? GetFileByUrl(string url)
    {
        return fileBedCollection.FindOne(x => x.Url == url);
    }
}
