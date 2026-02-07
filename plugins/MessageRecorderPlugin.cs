using BotPlugin;
using GroupHistoryRecorder;
using NapcatClient;
using NapcatClient.MessageType;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("MessageRecorder", "自动记录所有群聊消息到 LiteDB 数据库",priority:1000,type:PluginType.Background)]
public class MessageRecorderPlugin : Plugin
{
    private HistoryRecorder historyRecorder;
    
    public MessageRecorderPlugin(PluginInterop interop) : base(interop)
    {
        string dbPath = Path.Combine(interop.PathPrefix, "group_history.db");
        
        historyRecorder = new HistoryRecorder(dbPath);
        Logger.Info($"MessageRecorderPlugin 初始化完成，数据库路径: {dbPath}");
    }
    
    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        try
        {
            // 将接收到的消息转换为 GroupMessage 模型
            var groupMessage = GroupHistoryRecorder.GroupMessage.FromReceivedGroupMessage(data);
            
            // 记录消息到数据库（消息链已包含在 GroupMessage.Messages 中）
            var success = historyRecorder.RecordMessage(groupMessage);
            
            if (success)
            {
                // 处理消息中的图片、文件等资源（下载并存储到 ImageEntry/FileEntry）
                _ = ProcessMessageResources(data.message, groupId);
                
                Logger.Debug($"记录消息: 群 {groupId}, 发送者 {data.sender.user_id}, 消息ID {data.message_id}");
            }
            else
            {
                Logger.Debug($"消息已存在，跳过记录: 消息ID {data.message_id}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"记录消息失败: {ex.Message}");
        }
    }
    
    private async Task ProcessMessageResources(System.Collections.Generic.IEnumerable<TypedMessage> messages, long groupId, int depth = 0)
    {
        if (depth > 5) // 防止递归过深
            return;
        
        foreach (var message in messages)
        {
            try
            {
                if (message is ImageData imageData)
                {
                    await ProcessImageMessage(imageData);
                }
                else if (message is FileData fileData)
                {
                    await ProcessFileMessage(fileData);
                }
                else if (message is ForwardData forwardData)
                {
                    await ProcessForwardMessage(forwardData, groupId, depth + 1);
                }
                else if (message is ReplyData replyData)
                {
                    await ProcessReplyMessage(replyData, groupId, depth + 1);
                }
                else if (message is NapcatClient.MessageType.VideoData videoData)
                {
                    await ProcessVideoMessage(videoData);
                }
                else if (message is NapcatClient.MessageType.RecordData recordData)
                {
                    await ProcessRecordMessage(recordData);
                }
                // 其他消息类型（如文本、@、表情等）不需要特别处理，它们已经包含在消息链中
            }
            catch (Exception ex)
            {
                Logger.Error($"处理消息资源失败: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessImageMessage(ImageData imageData)
    {
        if (!string.IsNullOrEmpty(imageData.Url))
        {
            try
            {
                // 检查图片是否已存在
                if (historyRecorder.GetImageByUrl(imageData.Url) == null)
                {
                    // 下载图片，使用 Actions 提供的 HTTP 接口
                    var imageDataBytes = await Actions.HttpGetBinary(imageData.Url);
                    
                    // 存储到 ImageEntry
                    var imageEntry = new GroupHistoryRecorder.ImageEntry(imageData.Url, imageDataBytes);
                    historyRecorder.RecordImage(imageEntry);
                    
                    Logger.Debug($"已存储图片: {imageData.Url}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"下载图片失败: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessFileMessage(FileData fileData)
    {
        if (!string.IsNullOrEmpty(fileData.File))
        {
            try
            {
                // 检查文件是否已存在
                if (historyRecorder.GetFileByUrl(fileData.File) == null)
                {
                    // 下载文件，使用 Actions 提供的 HTTP 接口
                    var fileDataBytes = await Actions.HttpGetBinary(fileData.File);
                    
                    // 存储到 FileEntry
                    var fileEntry = new GroupHistoryRecorder.FileEntry(fileData.File, fileDataBytes);
                    historyRecorder.RecordFile(fileEntry);
                    
                    Logger.Debug($"已存储文件: {fileData.File}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"下载文件失败: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessVideoMessage(NapcatClient.MessageType.VideoData videoData)
    {
        if (!string.IsNullOrEmpty(videoData.File))
        {
            try
            {
                // 检查文件是否已存在
                if (historyRecorder.GetFileByUrl(videoData.File) == null)
                {
                    // 下载视频，使用 Actions 提供的 HTTP 接口
                    var videoDataBytes = await Actions.HttpGetBinary(videoData.File);
                    
                    // 存储到 FileEntry
                    var fileEntry = new GroupHistoryRecorder.FileEntry(videoData.File, videoDataBytes);
                    historyRecorder.RecordFile(fileEntry);
                    
                    Logger.Debug($"已存储视频: {videoData.File}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"下载视频失败: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessRecordMessage(NapcatClient.MessageType.RecordData recordData)
    {
        if (!string.IsNullOrEmpty(recordData.File))
        {
            try
            {
                // 检查文件是否已存在
                if (historyRecorder.GetFileByUrl(recordData.File) == null)
                {
                    // 下载语音，使用 Actions 提供的 HTTP 接口
                    var recordDataBytes = await Actions.HttpGetBinary(recordData.File);
                    
                    // 存储到 FileEntry
                    var fileEntry = new GroupHistoryRecorder.FileEntry(recordData.File, recordDataBytes);
                    historyRecorder.RecordFile(fileEntry);
                    
                    Logger.Debug($"已存储语音: {recordData.File}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"下载语音失败: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessForwardMessage(ForwardData forwardData, long groupId, int depth)
    {
        try
        {
            // 获取转发消息内容
            var forwardMessage = await Actions.GetForwardMessageById(forwardData.Id);
            if (forwardMessage != null)
            {
                foreach (var msg in forwardMessage.Messages)
                {
                    // 检查消息是否已存在，避免重复存储
                    if (!historyRecorder.MessageExists(msg.MessageId))
                    {
                        // 由于 msg 可能不是 ReceivedGroupMessage 类型，这里只处理资源，不存储消息本身
                        // 消息本身会在接收到时被存储
                        Logger.Debug($"转发消息已存在或类型不支持，仅处理资源: {msg.MessageId}");
                    }
                    
                    // 递归处理转发消息中的资源
                    await ProcessMessageResources(msg.Message, groupId, depth);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"处理转发消息失败: {ex.Message}");
        }
    }
    
    private async Task ProcessReplyMessage(ReplyData replyData, long groupId, int depth)
    {
        try
        {
            // 获取回复消息内容
            var replyMessage = await Actions.GetMessageById(replyData.Id);
            if (replyMessage != null)
            {
                // 检查消息是否已存在，避免重复存储
                if (!historyRecorder.MessageExists(replyMessage.MessageId))
                {
                    // 由于 replyMessage 可能不是 ReceivedGroupMessage 类型，这里只处理资源，不存储消息本身
                    // 消息本身会在接收到时被存储
                    Logger.Debug($"回复消息已存在或类型不支持，仅处理资源: {replyMessage.MessageId}");
                }
                
                // 递归处理回复消息中的资源
                await ProcessMessageResources(replyMessage.Message, groupId, depth);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"处理回复消息失败: {ex.Message}");
        }
    }
    
    public override void Dispose()
    {
        historyRecorder.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}