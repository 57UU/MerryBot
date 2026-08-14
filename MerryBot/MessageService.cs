using BotPlugin;
using DataService;
using NapcatClient;
using NapcatClient.Action;
using NapcatClient.EventType;
using NapcatClient.MessageType;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StoredForward = DataService.ForwardMessageEntry;
using StoredMessage = DataService.GroupMessage;

namespace MerryBot;

/// <summary>
/// Core 的消息本地化、持久化和按需读取服务。
/// 入站路径只创建内存快照；所有数据库与远端 I/O 都在后台任务内完成。
/// </summary>
internal sealed class MessageService : IMessageService
{
    private const long FileSizeLimit = 20 * 1024 * 1024;

    private readonly Actions bot;
    private readonly HistoryRecorder history;
    private readonly NLog.Logger logger;
    private readonly ConcurrentDictionary<MessageKey, ProcessedMessage> messages = new();
    private readonly ConcurrentDictionary<MessageKey, Lazy<Task<ProcessedMessage?>>> messageLoads = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessedForwardMessage?>>> forwardLoads = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<LocalMessageResource?>>> resourceLoads = new();
    private readonly ConcurrentDictionary<string, ResourceDescriptor> resourceDescriptors = new();
    private readonly ConcurrentDictionary<string, ForwardSeed> forwardSeeds = new();
    private readonly ConcurrentDictionary<long, DateTime> groupInfoRefreshes = new();

    public MessageService(Actions bot, HistoryRecorder history, NLog.Logger logger)
    {
        this.bot = bot;
        this.history = history;
        this.logger = logger;
    }

    public MessageIngress Ingest(ReceivedGroupMessage raw)
    {
        var localized = LocalizeChain(raw.message, raw.GroupId);
        var snapshot = CreateSnapshot(
            raw.GroupId,
            raw.message_id,
            raw.sender.user_id,
            raw.sender.nickname,
            raw.sender.card,
            raw.sender.role,
            localized.Chain,
            DateTimeOffset.FromUnixTimeSeconds(raw.time).UtcDateTime,
            false);

        // 真正到达的入站消息优先于同时进行的 get_msg 请求。
        messages[new MessageKey(raw.GroupId, raw.message_id)] = snapshot;
        var ingress = new MessageIngress(snapshot, localized.Resources, localized.Forwards);
        _ = PersistIngressAsync(ingress);
        return ingress;
    }

    public async Task PrefetchAsync(MessageIngress ingress)
    {
        try
        {
            var work = new List<Task>();
            foreach (var resource in ingress.Resources)
            {
                work.Add(GetResourceAsync(resource.LocalUri));
            }
            foreach (var forward in ingress.Forwards)
            {
                work.Add(GetForwardAsync(forward.ForwardId, forward.SourceGroupId));
            }
            foreach (var item in ingress.Message.MessageChain)
            {
                if (item is ReplyData reply)
                {
                    work.Add(GetReplyAsync(ingress.Message.GroupId, reply.Id));
                }
            }
            await Task.WhenAll(work);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "消息后台预取失败");
        }
    }

    public Task<ProcessedMessage?> GetReplyAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default)
        => GetMessageAsync(groupId, messageIdOrReference, cancellationToken);

    public async Task<ProcessedMessage?> GetMessageAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default)
    {
        if (LocalMessageReference.TryParseMessage(messageIdOrReference, out var referenceGroupId, out var referenceMessageId))
        {
            groupId = referenceGroupId;
            messageIdOrReference = referenceMessageId.ToString();
        }
        if (!long.TryParse(messageIdOrReference, out var messageId)) return null;

        var key = new MessageKey(groupId, messageId);
        if (messages.TryGetValue(key, out var local)) return CloneSnapshot(local);

        var loader = messageLoads.GetOrAdd(key, static (messageKey, self) =>
            new Lazy<Task<ProcessedMessage?>>(() => self.LoadMessageAsync(messageKey), LazyThreadSafetyMode.ExecutionAndPublication), this);
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            return result == null ? null : CloneSnapshot(result);
        }
        catch (Exception ex)
        {
            messageLoads.TryRemove(key, out _);
            logger.Warn(ex, "读取回复消息失败: {0}", key);
            return null;
        }
    }

    public async Task<ProcessedForwardMessage?> GetForwardAsync(string forwardIdOrReference, long sourceGroupId, CancellationToken cancellationToken = default)
    {
        var forwardId = LocalMessageReference.TryParseForward(forwardIdOrReference, out var parsed)
            ? parsed
            : forwardIdOrReference;
        if (string.IsNullOrWhiteSpace(forwardId)) return null;

        var loader = forwardLoads.GetOrAdd(forwardId, static (id, state) =>
            new Lazy<Task<ProcessedForwardMessage?>>(() => state.self.LoadForwardAsync(id, state.sourceGroupId), LazyThreadSafetyMode.ExecutionAndPublication), (self: this, sourceGroupId));
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            return result == null ? null : CloneForward(result);
        }
        catch (Exception ex)
        {
            forwardLoads.TryRemove(forwardId, out _);
            logger.Warn(ex, "读取合并转发失败: {0}", forwardId);
            return null;
        }
    }

    public async Task<LocalMessageResource?> GetResourceAsync(string localUri, CancellationToken cancellationToken = default)
    {
        if (!LocalMessageReference.IsResource(localUri)) return null;
        var loader = resourceLoads.GetOrAdd(localUri, static (uri, self) =>
            new Lazy<Task<LocalMessageResource?>>(() => self.LoadResourceAsync(uri), LazyThreadSafetyMode.ExecutionAndPublication), this);
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            return result == null ? null : result with { Data = result.Data.ToArray() };
        }
        catch (Exception ex)
        {
            resourceLoads.TryRemove(localUri, out _);
            logger.Warn(ex, "读取消息资源失败: {0}", localUri);
            return null;
        }
    }

    public void RecordGroupAdmin(GroupAdminEvent eventData) => RecordEvent(eventData.GroupId, "group_admin", eventData.SubType, eventData.UserId, eventData.UserId, null, null, eventData.Time);
    public void RecordGroupDecrease(GroupDecreaseEvent eventData) => RecordEvent(eventData.GroupId, "group_decrease", eventData.SubType, eventData.UserId, eventData.OperatorId, null, null, eventData.Time);
    public void RecordGroupIncrease(GroupIncreaseEvent eventData) => RecordEvent(eventData.GroupId, "group_increase", eventData.SubType, eventData.UserId, eventData.OperatorId, null, null, eventData.Time);
    public void RecordGroupBan(GroupBanEvent eventData) => RecordEvent(eventData.GroupId, "group_ban", eventData.SubType, eventData.UserId, eventData.OperatorId, null, eventData.Duration, eventData.Time);

    public void RecordGroupRecall(GroupRecallEvent eventData)
    {
        RecordEvent(eventData.GroupId, "group_recall", "recall", eventData.UserId, eventData.OperatorId, eventData.MessageId, null, eventData.Time);
        _ = MarkRecallAsync(eventData.GroupId, eventData.MessageId);
    }

    private async Task PersistIngressAsync(MessageIngress ingress)
    {
        try
        {
            await history.UpsertMessageAsync(ToStoredMessage(ingress.Message));
            foreach (var resource in ingress.Resources)
            {
                resourceDescriptors.TryAdd(resource.LocalUri, resource);
                await history.UpsertResourceReferenceAsync(resource.ToStorageModel());
            }
            foreach (var forward in ingress.Forwards)
            {
                forwardSeeds.TryAdd(forward.ForwardId, forward);
            }
            _ = RefreshGroupInfoAsync(ingress.Message.GroupId);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "保存消息失败: {0}", ingress.Message.MessageId);
        }
    }

    private async Task<ProcessedMessage?> LoadMessageAsync(MessageKey key)
    {
        if (messages.TryGetValue(key, out var local)) return local;
        var stored = await history.GetMessageByIdAsync(key.MessageId, key.GroupId);
        if (stored != null)
        {
            var restored = FromStoredMessage(stored);
            return messages.GetOrAdd(key, restored);
        }

        var remote = await bot.GetMessageById(key.MessageId.ToString());
        if (remote == null) return null;
        if (messages.TryGetValue(key, out local)) return local;

        var localized = LocalizeChain(remote.Message, key.GroupId);
        var fetched = CreateSnapshot(
            key.GroupId,
            remote.MessageId,
            remote.UserId,
            remote.SenderInfo.nickname,
            remote.SenderInfo.card,
            remote.SenderInfo.role,
            localized.Chain,
            DateTimeOffset.FromUnixTimeSeconds(remote.Time).UtcDateTime,
            false);
        var winner = messages.GetOrAdd(key, fetched);
        if (ReferenceEquals(winner, fetched))
        {
            await history.UpsertMessageAsync(ToStoredMessage(fetched));
            foreach (var resource in localized.Resources)
            {
                resourceDescriptors.TryAdd(resource.LocalUri, resource);
                await history.UpsertResourceReferenceAsync(resource.ToStorageModel());
            }
            foreach (var forward in localized.Forwards) forwardSeeds.TryAdd(forward.ForwardId, forward);
        }
        return winner;
    }

    private async Task<ProcessedForwardMessage?> LoadForwardAsync(string forwardId, long sourceGroupId)
    {
        var stored = await history.GetForwardMessageByIdAsync(forwardId);
        if (stored != null) return FromStoredForward(stored);

        if (!forwardSeeds.TryGetValue(forwardId, out var seed))
        {
            var remote = await bot.GetForwardMessageById(forwardId);
            if (remote == null || remote.Messages.Count == 0) return null;
            seed = new ForwardSeed(forwardId, sourceGroupId, remote.Messages.Select(CreateForwardSource).ToList());
            forwardSeeds.TryAdd(forwardId, seed);
        }

        var forward = BuildForward(seed);
        await history.RecordForwardMessageAsync(ToStoredForward(forward));
        return forward;
    }

    private async Task<LocalMessageResource?> LoadResourceAsync(string localUri)
    {
        var reference = await history.GetResourceReferenceAsync(localUri);
        if (reference?.StoredObjectId is long objectId)
        {
            return await ReadStoredResourceAsync(localUri, reference.Kind, reference.OriginalName, reference.IsImage, objectId);
        }

        if (!resourceDescriptors.TryGetValue(localUri, out var descriptor))
        {
            return null;
        }
        reference ??= descriptor.ToStorageModel();
        reference = await history.UpsertResourceReferenceAsync(reference);
        if (reference.StoredObjectId is long existingObjectId)
        {
            return await ReadStoredResourceAsync(localUri, reference.Kind, reference.OriginalName, reference.IsImage, existingObjectId);
        }
        if (string.IsNullOrWhiteSpace(descriptor.Source)) return null;

        var bytes = await bot.HttpGetBinary(descriptor.Source);
        if (bytes.LongLength > FileSizeLimit)
        {
            logger.Info("消息资源过大，跳过保存: {0}", localUri);
            return null;
        }

        if (descriptor.IsImage)
        {
            var image = await history.RecordImageAsync(descriptor.Source, bytes);
            reference.StoredObjectId = image.Id;
            reference.IsImage = true;
        }
        else
        {
            var file = await history.RecordFileAsync(descriptor.Source, bytes);
            reference.StoredObjectId = file.Id;
            reference.IsImage = false;
        }
        reference.UpdatedTime = DateTime.UtcNow;
        await history.UpsertResourceReferenceAsync(reference);
        return new LocalMessageResource(localUri, descriptor.Kind, descriptor.OriginalName, GetContentType(descriptor.Kind, descriptor.OriginalName ?? descriptor.Source), bytes);
    }

    private async Task<LocalMessageResource?> ReadStoredResourceAsync(string localUri, string kind, string? originalName, bool isImage, long objectId)
    {
        if (isImage)
        {
            var image = await history.GetImageByIdAsync(objectId);
            if (image == null) return null;
            var data = await history.GetImageDataAsync(image.Hash);
            return data == null ? null : new LocalMessageResource(localUri, kind, originalName, GetContentType(kind, image.OriginalUrl), data);
        }

        var file = await history.GetFileByIdAsync(objectId);
        if (file == null) return null;
        var fileData = await history.GetFileDataAsync(file.Hash);
        return fileData == null ? null : new LocalMessageResource(localUri, kind, originalName, GetContentType(kind, file.OriginalUrl), fileData);
    }

    private LocalizedChain LocalizeChain(IEnumerable<TypedMessage> source, long groupId)
    {
        var chain = new List<TypedMessage>();
        var resources = new List<ResourceDescriptor>();
        var forwards = new List<ForwardSeed>();
        foreach (var original in source)
        {
            var item = original.Clone();
            switch (item)
            {
                case ReplyData reply:
                    if (long.TryParse(reply.Id, out var replyId)) reply.Id = LocalMessageReference.Message(groupId, replyId);
                    break;
                case ForwardData forward:
                    var forwardId = forward.Id;
                    if (forward.Content is { Count: > 0 })
                    {
                        var seed = new ForwardSeed(forwardId, groupId, forward.Content.Select(CreateForwardSource).ToList());
                        forwards.Add(seed);
                        forwardSeeds.TryAdd(forwardId, seed);
                    }
                    forward.Content = null;
                    forward.Id = LocalMessageReference.Forward(forwardId);
                    break;
                case ImageData image:
                    var imageUri = RegisterResource("image", image.Url ?? image.File, image.File, true, resources);
                    image.Url = imageUri;
                    image.File = imageUri;
                    break;
                case FileData file:
                    var fileUri = RegisterResource("file", file.Url, file.File, false, resources);
                    file.Url = fileUri;
                    file.FileId = fileUri;
                    break;
                case RecordData record:
                    var recordUri = RegisterResource("record", record.Url, record.File, false, resources);
                    record.Url = recordUri;
                    record.Path = recordUri;
                    break;
                case VideoData video:
                    var videoUri = RegisterResource("video", video.Url ?? video.File, video.File, false, resources);
                    video.Url = videoUri;
                    video.File = videoUri;
                    if (!string.IsNullOrWhiteSpace(video.Thumb)) video.Thumb = RegisterResource("image", video.Thumb, null, true, resources);
                    break;
            }
            chain.Add(item);
        }
        return new LocalizedChain(chain, resources, forwards);
    }

    private string RegisterResource(string kind, string? source, string? originalName, bool isImage, List<ResourceDescriptor> resources)
    {
        if (LocalMessageReference.IsResource(source ?? string.Empty)) return source!;
        source ??= string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{source}"))).ToLowerInvariant();
        var localUri = LocalMessageReference.Resource(kind, hash);
        var descriptor = new ResourceDescriptor(localUri, kind, source, originalName, isImage);
        resourceDescriptors.TryAdd(localUri, descriptor);
        resources.Add(descriptor);
        return localUri;
    }

    private string RegisterLegacyResource(string kind, long storedObjectId, string? originalName, bool isImage, List<ResourceDescriptor> resources)
    {
        var localUri = LocalMessageReference.Resource(kind, $"legacy-{storedObjectId}");
        var descriptor = new ResourceDescriptor(localUri, kind, string.Empty, originalName, isImage, storedObjectId);
        resourceDescriptors.TryAdd(localUri, descriptor);
        resources.Add(descriptor);
        return localUri;
    }

    private LocalizedChain LocalizeStoredChain(IEnumerable<TypedMessage> source, long groupId)
    {
        var chain = new List<TypedMessage>();
        var resources = new List<ResourceDescriptor>();
        foreach (var original in source)
        {
            var item = original.Clone();
            switch (item)
            {
                case ReplyData reply when long.TryParse(reply.Id, out var replyId):
                    reply.Id = LocalMessageReference.Message(groupId, replyId);
                    break;
                case ForwardData forward:
                    if (!LocalMessageReference.TryParseForward(forward.Id, out _)) forward.Id = LocalMessageReference.Forward(forward.Id);
                    forward.Content = null;
                    break;
                case ImageData image when long.TryParse(image.Url, out var imageId):
                    var imageUri = RegisterLegacyResource("image", imageId, image.File, true, resources);
                    image.Url = imageUri;
                    image.File = imageUri;
                    break;
                case FileData file when long.TryParse(file.Url, out var fileId):
                    var fileUri = RegisterLegacyResource("file", fileId, file.File, false, resources);
                    file.Url = fileUri;
                    file.FileId = fileUri;
                    break;
                case RecordData record when long.TryParse(record.Url, out var recordId):
                    var recordUri = RegisterLegacyResource("record", recordId, record.File, false, resources);
                    record.Url = recordUri;
                    record.Path = recordUri;
                    break;
                case VideoData video when long.TryParse(video.Url, out var videoId):
                    var videoUri = RegisterLegacyResource("video", videoId, video.File, false, resources);
                    video.Url = videoUri;
                    video.File = videoUri;
                    break;
            }
            chain.Add(item);
        }
        return new LocalizedChain(chain, resources, []);
    }

    private ProcessedForwardMessage BuildForward(ForwardSeed seed)
    {
        var entries = new List<ProcessedMessage>();
        foreach (var source in seed.Messages)
        {
            var localized = LocalizeChain(source.MessageChain, seed.SourceGroupId);
            foreach (var resource in localized.Resources)
            {
                resourceDescriptors.TryAdd(resource.LocalUri, resource);
                _ = GetResourceAsync(resource.LocalUri);
            }
            foreach (var forward in localized.Forwards)
            {
                forwardSeeds.TryAdd(forward.ForwardId, forward);
                _ = GetForwardAsync(forward.ForwardId, forward.SourceGroupId);
            }
            foreach (var reply in localized.Chain.OfType<ReplyData>()) _ = GetReplyAsync(seed.SourceGroupId, reply.Id);
            entries.Add(CreateSnapshot(seed.SourceGroupId, source.MessageId, source.SenderId, source.Nickname, source.Card, source.Role, localized.Chain, source.Time, false));
        }
        var time = entries.Count > 0 ? entries.Min(static entry => entry.Time) : DateTime.UtcNow;
        return new ProcessedForwardMessage(LocalMessageReference.Forward(seed.ForwardId), seed.SourceGroupId, entries, time);
    }

    private async Task RefreshGroupInfoAsync(long groupId)
    {
        var now = DateTime.UtcNow;
        if (groupInfoRefreshes.TryGetValue(groupId, out var last) && now - last < TimeSpan.FromHours(24)) return;
        groupInfoRefreshes[groupId] = now;
        try
        {
            var info = await bot.GetGroupInfo(groupId.ToString());
            if (info == null) return;
            await history.RecordOrUpdateGroupNameAsync(new GroupNameEntry
            {
                GroupId = groupId,
                Name = info.GroupName,
                MemberCount = info.MemberCount,
                MaxMemberCount = info.MaxMemberCount,
                UpdatedTime = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "更新群信息失败: {0}", groupId);
        }
    }

    private void RecordEvent(long groupId, string eventType, string subType, long userId, long operatorId, long? messageId, long? duration, long unixTime)
        => _ = RecordEventAsync(new GroupEvent(groupId, eventType, subType, userId, operatorId, messageId, duration, null, DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime));

    private async Task RecordEventAsync(GroupEvent groupEvent)
    {
        try { await history.RecordGroupEventAsync(groupEvent); }
        catch (Exception ex) { logger.Error(ex, "记录群事件失败: {0}", groupEvent.EventType); }
    }

    private async Task MarkRecallAsync(long groupId, long messageId)
    {
        try { await history.MarkMessageAsDeletedAsync(messageId, groupId); }
        catch (Exception ex) { logger.Warn(ex, "标记撤回消息失败: {0}", messageId); }
    }

    private static ProcessedMessage CreateSnapshot(long groupId, long messageId, long senderId, string nickname, string card, string role, IReadOnlyList<TypedMessage> chain, DateTime time, bool deleted)
        => new(groupId, messageId, senderId, nickname ?? string.Empty, card ?? string.Empty, role ?? string.Empty, CloneChain(chain), time, deleted);

    private static ProcessedMessage CloneSnapshot(ProcessedMessage source)
        => source with { MessageChain = CloneChain(source.MessageChain) };

    private static ProcessedForwardMessage CloneForward(ProcessedForwardMessage source)
        => source with { Messages = source.Messages.Select(CloneSnapshot).ToList() };

    private static IReadOnlyList<TypedMessage> CloneChain(IReadOnlyList<TypedMessage> source)
        => source.Select(item => item.Clone()).ToList();

    private static StoredMessage ToStoredMessage(ProcessedMessage source)
        => new(source.GroupId, source.SenderId, source.SenderNickname, source.SenderGroupNickname, source.SenderGroupRole, source.MessageId, CloneChain(source.MessageChain).ToList(), source.Time, source.IsDeleted);

    private ProcessedMessage FromStoredMessage(StoredMessage source)
    {
        var localized = LocalizeStoredChain(source.Messages, source.GroupId);
        foreach (var resource in localized.Resources) resourceDescriptors.TryAdd(resource.LocalUri, resource);
        return CreateSnapshot(source.GroupId, source.MessageId, source.SenderId, source.SenderNickname, source.SenderGroupNickname, source.SenderGroupRole, localized.Chain, source.Time, source.IsDeleted);
    }

    private static StoredForward ToStoredForward(ProcessedForwardMessage source)
    {
        var forwardId = LocalMessageReference.TryParseForward(source.Id, out var parsed) ? parsed : source.Id;
        return new StoredForward(forwardId, source.SourceGroupId, source.Messages.Select(ToStoredMessage).ToList(), source.Time);
    }

    private ProcessedForwardMessage FromStoredForward(StoredForward source)
        => new(LocalMessageReference.Forward(source.ForwardId), source.SourceGroupId, source.Messages.Select(FromStoredMessage).ToList(), source.Time);

    private static ForwardSource CreateForwardSource(NapcatClient.GroupMessage source)
        => new(source.MessageId, source.UserId, source.SenderInfo.nickname, source.SenderInfo.card, source.SenderInfo.role, source.Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(source.Time).UtcDateTime : DateTime.UtcNow, source.Message.Select(item => item.Clone()).ToList());

    private static string GetContentType(string kind, string? name)
    {
        var extension = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        if (kind == "image") return extension switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg" };
        return extension switch { ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".mp4" => "video/mp4", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    }

    internal sealed record MessageIngress(ProcessedMessage Message, IReadOnlyList<ResourceDescriptor> Resources, IReadOnlyList<ForwardSeed> Forwards)
    {
        public IReadOnlyList<TypedMessage> MessageChain => Message.MessageChain;
    }

    internal sealed record ResourceDescriptor(string LocalUri, string Kind, string Source, string? OriginalName, bool IsImage, long? StoredObjectId = null)
    {
        public ResourceReference ToStorageModel() => new()
        {
            LocalUri = LocalUri,
            Kind = Kind,
            Source = Source,
            OriginalName = OriginalName,
            IsImage = IsImage,
            StoredObjectId = StoredObjectId,
            UpdatedTime = DateTime.UtcNow
        };
    }

    internal sealed record ForwardSeed(string ForwardId, long SourceGroupId, IReadOnlyList<ForwardSource> Messages);
    internal sealed record ForwardSource(long MessageId, long SenderId, string Nickname, string Card, string Role, DateTime Time, IReadOnlyList<TypedMessage> MessageChain);
    private sealed record LocalizedChain(IReadOnlyList<TypedMessage> Chain, IReadOnlyList<ResourceDescriptor> Resources, IReadOnlyList<ForwardSeed> Forwards);
    private readonly record struct MessageKey(long GroupId, long MessageId);
}
