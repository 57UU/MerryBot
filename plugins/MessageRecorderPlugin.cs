using BotPlugin;
using NapcatClient;
using NapcatClient.EventType;
using NapcatClient.MessageType;
using System;
using System.IO;
using System.Threading.Tasks;
using DataService;

namespace BotPlugin;

[PluginTag("MessageRecorder", "自动记录所有群聊消息到 LiteDB 数据库", priority: 1000, type: PluginType.Background)]
public class MessageRecorderPlugin : Plugin
{
    private HistoryRecorder historyRecorder;
    public long FileSizeLimit { get; set; } = 1024 * 1024 * 20; // 10MB
    
    public MessageRecorderPlugin(PluginInterop interop) : base(interop)
    {
        interop.OnRawGroupMessageReceivedRegister(OnRawGroupMessageReceived);
    }
    private void OnRawGroupMessageReceived(ReceivedGroupMessage data)
    {
        _ = HandleGroupMessageAsync(data.group_id, data);
    }
    
    public override async Task OnLoaded()
    {
        // 从StorageManagerPlugin获取HistoryRecorder实例
        var storageManager = Interop.FindPlugin<StorageManagerPlugin>();
        if (storageManager == null)
        {
            throw new PluginNotUsableException("StorageManagerPlugin未找到，MessageRecorderPlugin需要StorageManagerPlugin初始化");
        }
        
        historyRecorder = storageManager.GroupHistoryRecorder;
        Logger.Info("MessageRecorderPlugin 初始化完成，使用StorageManagerPlugin提供的HistoryRecorder");
    }
    private readonly ThreadLocal<Random> _randomWrapper = new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));
    private Task DelayRandomTime()
    {
        return Task.Delay(_randomWrapper.Value!.Next(1000, 5000));
    }

    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        //use raw data
        //_ = HandleGroupMessageAsync(groupId, data);
    }
    
    private async Task HandleGroupMessageAsync(long groupId, ReceivedGroupMessage data)
    {
        await DelayRandomTime();
        try
        {
            // 克隆消息以避免修改原始数据
            var clonedMessage = CloneReceivedGroupMessage(data);
            
            // 处理消息中的资源并替换URL为内部ID（包括转发消息和回复消息的内容）
            await ProcessMessageResources(clonedMessage.message, groupId);

            // 将接收到的消息转换为 GroupMessage 模型（在资源处理之后，确保转发消息内容已保存）
            var groupMessage = DataService.GroupMessage.FromReceivedGroupMessage(clonedMessage);

            // 记录消息到数据库（消息链已包含在 GroupMessage.Messages 中）
            var success = historyRecorder.RecordMessage(groupMessage);

            if (success)
            {
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
    
    private ReceivedGroupMessage CloneReceivedGroupMessage(ReceivedGroupMessage original)
    {
        // 克隆消息数据
        var cloned = new ReceivedGroupMessage
        {
            group_id = original.group_id,
            message_id = original.message_id,
            self_id = original.self_id,
            time = original.time,
            sender = original.sender,
            message = new List<NapcatClient.MessageType.TypedMessage>()
        };
        
        // 克隆消息链
        foreach (var msg in original.message)
        {
            cloned.message.Add(msg.Clone());
        }
        
        return cloned;
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
                // 下载图片，使用 Actions 提供的 HTTP 接口
                var imageDataBytes = await Actions.HttpGetBinary(imageData.Url);

                // 检查文件大小
                if (imageDataBytes.Length > FileSizeLimit)
                {
                    Logger.Trace($"图片文件过大，跳过存储: {imageData.Url}, 大小: {imageDataBytes.Length} bytes");
                    return;
                }

                // 存储图片并获取内部ID
                var imageEntry = historyRecorder.RecordImage(imageData.Url, imageDataBytes);
                
                // 替换URL为内部ID（字符串形式）
                imageData.Url = imageEntry.Id.ToString();
                if (!string.IsNullOrEmpty(imageData.File))
                {
                    imageData.File = imageEntry.Id.ToString();
                }

                Logger.Trace($"已存储图片: {imageEntry.OriginalUrl} -> 内部ID: {imageEntry.Id}");
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
                if (fileData.FileSize > FileSizeLimit) {
                    Logger.Trace($"文件过大，跳过存储: {fileData.File}, 大小: {fileData.FileSize} bytes");
                    return;
                }
                // 下载文件，使用 Actions 提供的 HTTP 接口
                var fileDataBytes = await Actions.HttpGetBinary(fileData.Url);

                // 检查文件大小
                if (fileDataBytes.Length > FileSizeLimit)
                {
                    Logger.Trace($"文件过大，跳过存储: {fileData.File}, 大小: {fileDataBytes.Length} bytes");
                    return;
                }

                // 存储文件并获取内部ID
                var fileEntry = historyRecorder.RecordFile(fileData.Url, fileDataBytes);
                
                // 替换URL为内部ID（字符串形式）
                fileData.Url = fileEntry.Id.ToString();

                Logger.Trace($"已存储文件: {fileEntry.OriginalUrl} -> 内部ID: {fileEntry.Id}");
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
                // 下载视频，使用 Actions 提供的 HTTP 接口
                var videoDataBytes = await Actions.HttpGetBinary(videoData.File);

                // 检查文件大小
                if (videoDataBytes.Length > FileSizeLimit)
                {
                    Logger.Trace($"视频文件过大，跳过存储: {videoData.File}, 大小: {videoDataBytes.Length} bytes");
                    return;
                }

                // 存储视频并获取内部ID
                var fileEntry = historyRecorder.RecordFile(videoData.File, videoDataBytes);
                
                // 替换URL为内部ID（字符串形式）
                videoData.File = fileEntry.Id.ToString();

                Logger.Trace($"已存储视频: {fileEntry.OriginalUrl} -> 内部ID: {fileEntry.Id}");
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
                // 下载语音，使用 Actions 提供的 HTTP 接口
                var recordDataBytes = await Actions.HttpGetBinary(recordData.File);

                // 检查文件大小
                if (recordDataBytes.Length > FileSizeLimit)
                {
                    Logger.Trace($"语音文件过大，跳过存储: {recordData.File}, 大小: {recordDataBytes.Length} bytes");
                    return;
                }

                // 存储语音并获取内部ID
                var fileEntry = historyRecorder.RecordFile(recordData.File, recordDataBytes);
                
                // 替换URL为内部ID（字符串形式）
                recordData.File = fileEntry.Id.ToString();

                Logger.Trace($"已存储语音: {fileEntry.OriginalUrl} -> 内部ID: {fileEntry.Id}");
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
            if (historyRecorder.ForwardMessageExists(forwardData.Id))
            {
                Logger.Trace($"转发消息已存在: {forwardData.Id}");
                return;
            }
            
            var forwardMessage = await Actions.GetForwardMessageById(forwardData.Id);
            if (forwardMessage != null && forwardMessage.Messages.Any())
            {
                var messages = new List<DataService.GroupMessage>();
                foreach (var msg in forwardMessage.Messages)
                {
                    var groupMessage = DataService.GroupMessage.FromNapcatGroupMessage(msg);
                    messages.Add(groupMessage);
                    
                    await ProcessMessageResources(msg.Message, groupId, depth);
                }
                
                var time = DateTimeOffset.FromUnixTimeSeconds(forwardMessage.Messages.First().Time).UtcDateTime;
                var entry = new DataService.ForwardMessageEntry(
                    forwardData.Id,
                    groupId,
                    messages,
                    time
                );
                
                historyRecorder.RecordForwardMessage(entry);
                Logger.Debug($"保存转发消息: {forwardData.Id}, 包含 {messages.Count} 条消息");
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
                    // 转换并保存回复消息内容
                    var groupMessage = DataService.GroupMessage.FromNapcatGroupMessage(replyMessage);
                    historyRecorder.RecordMessage(groupMessage);
                    Logger.Trace($"保存回复消息: {replyMessage.MessageId}");
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

    public override void OnGroupAdminEvent(GroupAdminEvent eventData)
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(eventData.Time).UtcDateTime;
            var groupEvent = new GroupEvent(
                eventData.GroupId,
                "group_admin",
                eventData.SubType,
                eventData.UserId,
                eventData.UserId, // For admin events, UserId is both the target and operator
                null,
                null,
                null,
                time
            );

            var success = historyRecorder.RecordGroupEvent(groupEvent);
            if (success)
            {
                Logger.Debug($"记录群管理员事件: 群 {eventData.GroupId}, 操作 {eventData.SubType}, 用户 {eventData.UserId}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"记录群管理员事件失败: {ex.Message}");
        }
    }

    public override void OnGroupDecreaseEvent(GroupDecreaseEvent eventData)
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(eventData.Time).UtcDateTime;
            var groupEvent = new GroupEvent(
                eventData.GroupId,
                "group_decrease",
                eventData.SubType,
                eventData.UserId,
                eventData.OperatorId,
                null,
                null,
                null,
                time
            );

            var success = historyRecorder.RecordGroupEvent(groupEvent);
            if (success)
            {
                Logger.Debug($"记录群成员减少事件: 群 {eventData.GroupId}, 操作 {eventData.SubType}, 用户 {eventData.UserId}, 操作者 {eventData.OperatorId}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"记录群成员减少事件失败: {ex.Message}");
        }
    }

    public override void OnGroupIncreaseEvent(GroupIncreaseEvent eventData)
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(eventData.Time).UtcDateTime;
            var groupEvent = new GroupEvent(
                eventData.GroupId,
                "group_increase",
                eventData.SubType,
                eventData.UserId,
                eventData.OperatorId,
                null,
                null,
                null,
                time
            );

            var success = historyRecorder.RecordGroupEvent(groupEvent);
            if (success)
            {
                Logger.Debug($"记录群成员增加事件: 群 {eventData.GroupId}, 操作 {eventData.SubType}, 用户 {eventData.UserId}, 操作者 {eventData.OperatorId}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"记录群成员增加事件失败: {ex.Message}");
        }
    }

    public override void OnGroupBanEvent(GroupBanEvent eventData)
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(eventData.Time).UtcDateTime;
            var groupEvent = new GroupEvent(
                eventData.GroupId,
                "group_ban",
                eventData.SubType,
                eventData.UserId,
                eventData.OperatorId,
                null,
                eventData.Duration,
                null,
                time
            );

            var success = historyRecorder.RecordGroupEvent(groupEvent);
            if (success)
            {
                Logger.Debug($"记录群禁言事件: 群 {eventData.GroupId}, 操作 {eventData.SubType}, 用户 {eventData.UserId}, 操作者 {eventData.OperatorId}, 时长 {eventData.Duration}秒");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"记录群禁言事件失败: {ex.Message}");
        }
    }

    public override void OnGroupRecallEvent(GroupRecallEvent eventData)
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(eventData.Time).UtcDateTime;
            var groupEvent = new GroupEvent(
                eventData.GroupId,
                "group_recall",
                "recall",
                eventData.UserId,
                eventData.OperatorId,
                eventData.MessageId,
                null,
                null,
                time
            );

            var success = historyRecorder.RecordGroupEvent(groupEvent);
            if (success)
            {
                Logger.Debug($"记录群消息撤回事件: 群 {eventData.GroupId}, 消息发送者 {eventData.UserId}, 撤回操作者 {eventData.OperatorId}, 消息ID {eventData.MessageId}");
            }

            historyRecorder.MarkMessageAsDeleted(eventData.MessageId);
            Logger.Debug($"标记消息为已删除: 消息ID {eventData.MessageId}");
        }
        catch (Exception ex)
        {
            Logger.Error($"记录群消息撤回事件失败: {ex.Message}");
        }
    }


}
