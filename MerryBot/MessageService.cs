using BotPlugin;
using CommonLib;
using DataService;
using LlmBackend;
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
    private static readonly HttpClient httpClient = new();
    private readonly Actions bot;
    private readonly HistoryRecorder history;
    private readonly NLog.Logger logger;
    private readonly long _resourceSizeLimit;

    /// <summary>消息/转发/资源缓存过期时间；资源缓存额外受总字节上限约束。</summary>
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);
    /// <summary>消息资源（图片/文件）缓存总字节上限；超出后按 LRU 淘汰，防止大文件长期常驻内存。</summary>
    private const long ResourceCacheMaxBytes = 256L * 1024 * 1024;

    // 数据缓存：命中返回快照副本；未命中走下方 in-flight 合并加载，成功后写入缓存、失败可重试
    private readonly RequestCaching messageCache = new(CacheExpiration);
    private readonly RequestCaching forwardCache = new(CacheExpiration);
    private readonly RequestCaching resourceCache = new(CacheExpiration, sizeLimit: ResourceCacheMaxBytes,
        sizeProvider: static value => value is LocalMessageResource resource ? Math.Max(1, resource.Data.LongLength) : 1);
    // 加载中合并（同 key 并发只加载一次）；任务结束（成功/失败）后移除，结果落入上方缓存
    private readonly ConcurrentDictionary<MessageKey, Lazy<Task<ProcessedMessage?>>> messageInFlight = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessedForwardMessage?>>> forwardInFlight = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<LocalMessageResource?>>> resourceInFlight = new();
    private readonly ConcurrentDictionary<string, ResourceDescriptor> resourceDescriptors = new();
    private readonly ConcurrentDictionary<string, ForwardSeed> forwardSeeds = new();
    private readonly ConcurrentDictionary<long, DateTime> groupInfoRefreshes = new();

    public MessageService(Actions bot, HistoryRecorder history, NLog.Logger logger, long resourceSizeLimit)
    {
        this.bot = bot;
        this.history = history;
        this.logger = logger;
        _resourceSizeLimit = resourceSizeLimit;
    }


    /// <summary>记录一条 AI 会话消息到审计历史（仅文本内容，可带 token 用量）。messageType 为 user/assistant/tool。</summary>
    public Task RecordAiMessageAsync(string sessionKey, string messageType, string content, TokenUsage usage)
        => history.AiMessages.RecordAiMessageAsync(sessionKey, messageType, content,
            usage.promptUsage, usage.completionUsage, usage.cachedUsage);

    /// <summary>游标分页查询（按 MessageId 倒序）。before==null 取最新；否则取 MessageId &lt; anchor 的更早一页。已撤回消息保留，由调用方按 IsDeleted 标记展示。</summary>
    public async Task<IReadOnlyList<ProcessedMessage>> GetGroupMessagesBeforeAsync(long groupId, long? beforeMessageId, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        var result = new List<ProcessedMessage>(pageSize);
        var cursor = beforeMessageId;
        while (result.Count < pageSize)
        {
            var need = pageSize - result.Count;
            var stored = await history.GetMessagesByGroupIdBeforeAsync(groupId, cursor, need);
            if (stored.Count == 0) break;
            foreach (var m in stored)
            {
                result.Add(FromStoredMessage(m));
                if (result.Count == pageSize) break;
            }
            cursor = stored[^1].MessageId;
            if (stored.Count < need) break;
        }
        return result;
    }

    /// <summary>游标分页查询（按 ObjectId 倒序）。已撤回消息保留，由调用方按 IsDeleted 标记展示。</summary>
    public async Task<IReadOnlyList<ProcessedMessage>> GetGroupMessagesBeforeKeyAsync(long groupId, string? beforeMessageKey, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        var result = new List<ProcessedMessage>(pageSize);
        var cursorKey = beforeMessageKey;
        while (result.Count < pageSize)
        {
            var need = pageSize - result.Count;
            var stored = await history.GetMessagesByGroupIdBeforeKeyAsync(groupId, cursorKey, need);
            if (stored.Count == 0) break;
            foreach (var m in stored)
            {
                result.Add(FromStoredMessage(m));
                if (result.Count == pageSize) break;
            }
            cursorKey = stored[^1].Id.ToString();
            if (stored.Count < need) break;
        }
        return result;
    }

    /// <summary>群聊历史消息总数（含撤回消息）。</summary>
    public Task<int> GetGroupMessageCountAsync(long groupId, CancellationToken cancellationToken = default)
        => history.GetMessageCountByGroupIdAsync(groupId);

    private static string MessageCacheKey(MessageKey key) => $"msg:{key.GroupId}:{key.MessageId}";

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
        messageCache.SetCache(MessageCacheKey(new MessageKey(raw.GroupId, raw.message_id)), snapshot);
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

    public async Task<ProcessedMessage?> GetMessageByObjectIdAsync(string objectIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectIdHex)) return null;
        if (TryParseObjectId(objectIdHex, out _))
        {
            var stored = await history.GetMessageByObjectIdAsync(objectIdHex);
            if (stored != null) return FromStoredMessage(stored);
        }
        if (LocalMessageReference.TryParseMessage(objectIdHex, out var g, out var mid))
        {
            var byRef = await history.GetMessageByIdAsync(mid, g);
            if (byRef != null) return FromStoredMessage(byRef);
        }
        return null;
    }

    public async Task<ProcessedMessage?> GetMessageAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default)
    {
        if (LocalMessageReference.TryParseMessage(messageIdOrReference, out var referenceGroupId, out var referenceMessageId))
        {
            groupId = referenceGroupId;
            messageIdOrReference = referenceMessageId.ToString();
        }
        if (TryParseObjectId(messageIdOrReference, out _))
        {
            var byKey = await history.GetMessageByObjectIdAsync(messageIdOrReference);
            if (byKey != null) return FromStoredMessage(byKey);
        }
        if (!long.TryParse(messageIdOrReference, out var messageId)) return null;

        var key = new MessageKey(groupId, messageId);
        if (messageCache.TryGetCache<ProcessedMessage>(MessageCacheKey(key), out var local)) return CloneSnapshot(local);

        var loader = messageInFlight.GetOrAdd(key, static (messageKey, self) =>
            new Lazy<Task<ProcessedMessage?>>(() => self.LoadMessageAsync(messageKey), LazyThreadSafetyMode.ExecutionAndPublication), this);
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            // 无论成败都结束 in-flight：成功时结果已写入缓存，失败时不缓存下次可重试
            messageInFlight.TryRemove(key, out _);
            if (result == null)
            {
                // 瞬时失败（远端不可达等）不缓存 null，下次可重试
                return null;
            }
            return CloneSnapshot(result);
        }
        catch (Exception ex)
        {
            messageInFlight.TryRemove(key, out _);
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

        if (forwardCache.TryGetCache<ProcessedForwardMessage>(forwardId, out var cached)) return CloneForward(cached);

        var loader = forwardInFlight.GetOrAdd(forwardId, static (id, state) =>
            new Lazy<Task<ProcessedForwardMessage?>>(() => state.self.LoadForwardAsync(id, state.sourceGroupId), LazyThreadSafetyMode.ExecutionAndPublication), (self: this, sourceGroupId));
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            // 无论成败都结束 in-flight：成功时结果已写入缓存，失败时不缓存下次可重试
            forwardInFlight.TryRemove(forwardId, out _);
            if (result == null)
            {
                // 瞬时失败不缓存 null，下次可重试
                return null;
            }
            return CloneForward(result);
        }
        catch (Exception ex)
        {
            forwardInFlight.TryRemove(forwardId, out _);
            logger.Warn(ex, "读取合并转发失败: {0}", forwardId);
            return null;
        }
    }

    public async Task<LocalMessageResource?> GetResourceAsync(string localUri, CancellationToken cancellationToken = default)
    {
        if (!LocalMessageReference.IsResource(localUri)) return null;
        if (resourceCache.TryGetCache<LocalMessageResource>(localUri, out var cached)) return cached with { Data = cached.Data.ToArray() };

        var loader = resourceInFlight.GetOrAdd(localUri, static (uri, self) =>
            new Lazy<Task<LocalMessageResource?>>(() => self.LoadResourceAsync(uri), LazyThreadSafetyMode.ExecutionAndPublication), this);
        try
        {
            var result = await loader.Value.WaitAsync(cancellationToken);
            // 无论成败都结束 in-flight：成功时结果已写入缓存，失败时不缓存下次可重试
            resourceInFlight.TryRemove(localUri, out _);
            if (result == null)
            {
                // 瞬时失败不缓存 null，下次可重试
                return null;
            }
            return result with { Data = result.Data.ToArray() };
        }
        catch (Exception ex)
        {
            resourceInFlight.TryRemove(localUri, out _);
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
        var cacheKey = MessageCacheKey(key);
        if (messageCache.TryGetCache<ProcessedMessage>(cacheKey, out var local)) return local;
        var stored = await history.GetMessageByIdAsync(key.MessageId, key.GroupId);
        if (stored != null)
        {
            var restored = FromStoredMessage(stored);
            messageCache.SetCache(cacheKey, restored);
            return restored;
        }

        var remote = await bot.GetMessageById(key.MessageId.ToString());
        if (remote == null) return null;
        if (messageCache.TryGetCache<ProcessedMessage>(cacheKey, out local)) return local;

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
        messageCache.SetCache(cacheKey, fetched);
        // 仅当前缓存值仍是本次加载结果时执行持久化，避免并发加载重复写库（Upsert 幂等，即使重复也无副作用）
        if (ReferenceEquals(fetched, messageCache.TryGetCache<ProcessedMessage>(cacheKey, out var current) ? current : null))
        {
            await history.UpsertMessageAsync(ToStoredMessage(fetched));
            foreach (var resource in localized.Resources)
            {
                resourceDescriptors.TryAdd(resource.LocalUri, resource);
                await history.UpsertResourceReferenceAsync(resource.ToStorageModel());
            }
            foreach (var forward in localized.Forwards) forwardSeeds.TryAdd(forward.ForwardId, forward);
        }
        return fetched;
    }

    private async Task<ProcessedForwardMessage?> LoadForwardAsync(string forwardId, long sourceGroupId)
    {
        var stored = await history.GetForwardMessageByIdAsync(forwardId);
        if (stored != null)
        {
            var restored = FromStoredForward(stored);
            forwardCache.SetCache(forwardId, restored);
            return restored;
        }

        if (!forwardSeeds.TryGetValue(forwardId, out var seed))
        {
            var remote = await bot.GetForwardMessageById(forwardId);
            if (remote == null || remote.Messages.Count == 0) return null;
            seed = new ForwardSeed(forwardId, sourceGroupId, remote.Messages.Select(CreateForwardSource).ToList());
            forwardSeeds.TryAdd(forwardId, seed);
        }

        var forward = BuildForward(seed);
        await history.RecordForwardMessageAsync(ToStoredForward(forward));
        forwardCache.SetCache(forwardId, forward);
        return forward;
    }

    private async Task<LocalMessageResource?> LoadResourceAsync(string localUri)
    {
        if (resourceCache.TryGetCache<LocalMessageResource>(localUri, out var cachedResource)) return cachedResource;

        var reference = await history.GetResourceReferenceAsync(localUri);
        if (reference?.StoredObjectId is long objectId)
        {
            var storedResource = await ReadStoredResourceAsync(localUri, reference.Kind, reference.OriginalName, reference.IsImage, objectId);
            if (storedResource != null) resourceCache.SetCache(localUri, storedResource);
            return storedResource;
        }

        if (!resourceDescriptors.TryGetValue(localUri, out var descriptor))
        {
            return null;
        }
        reference ??= descriptor.ToStorageModel();
        reference = await history.UpsertResourceReferenceAsync(reference);
        if (reference.StoredObjectId is long existingObjectId)
        {
            var existingResource = await ReadStoredResourceAsync(localUri, reference.Kind, reference.OriginalName, reference.IsImage, existingObjectId);
            if (existingResource != null) resourceCache.SetCache(localUri, existingResource);
            return existingResource;
        }
        if (string.IsNullOrWhiteSpace(descriptor.Source)) return null;

        var bytes = await DownloadResourceAsync(descriptor.Source);
        if (bytes == null)
        {
            logger.Info("消息资源过大，跳过保存: {0}", localUri);
            return null;
        }

        string fileType = GetFileExtension(descriptor.OriginalName ?? descriptor.Source);
        if (descriptor.IsImage)
        {
            var image = await history.RecordImageAsync(bytes, fileType);
            reference.StoredObjectId = image.Id;
            reference.IsImage = true;
        }
        else
        {
            var file = await history.RecordFileAsync(bytes, fileType);
            reference.StoredObjectId = file.Id;
            reference.IsImage = false;
        }
        reference.UpdatedTime = DateTime.UtcNow;
        await history.UpsertResourceReferenceAsync(reference);
        var resource = new LocalMessageResource(localUri, descriptor.Kind, descriptor.OriginalName, GetContentType(descriptor.Kind, descriptor.OriginalName ?? descriptor.Source), bytes);
        resourceCache.SetCache(localUri, resource);
        return resource;
    }

    /// <summary>
    /// 下载消息资源：先用 Content-Length 预检（超过上限直接拒绝），
    /// 下载时按块流式读取并累计字节数，超过限制立即中断，避免整文件读入内存。
    /// 上限来自核心配置 ResourceSizeLimitMb。
    /// </summary>
    private async Task<byte[]?> DownloadResourceAsync(string source)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > _resourceSizeLimit)
        {
            logger.Info("消息资源 Content-Length 超过限制（{0} 字节），拒绝下载: {1}", contentLength, source);
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk);
            if (read <= 0) break;
            total += read;
            if (total > _resourceSizeLimit)
            {
                logger.Info("消息资源超过 {0} 字节，中断下载: {1}", _resourceSizeLimit, source);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<LocalMessageResource?> ReadStoredResourceAsync(string localUri, string kind, string? originalName, bool isImage, long objectId)
    {
        if (isImage)
        {
            var image = await history.GetImageByIdAsync(objectId);
            if (image == null) return null;
            var data = await history.GetImageDataAsync(image.Hash);
            var ext = string.IsNullOrEmpty(image.FileType) ? originalName : image.FileType;
            return data == null ? null : new LocalMessageResource(localUri, kind, originalName, GetContentType(kind, ext), data);
        }

        var file = await history.GetFileByIdAsync(objectId);
        if (file == null) return null;
        var fileData = await history.GetFileDataAsync(file.Hash);
        var fileExt = string.IsNullOrEmpty(file.FileType) ? originalName : file.FileType;
        return fileData == null ? null : new LocalMessageResource(localUri, kind, originalName, GetContentType(kind, fileExt), fileData);
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
        // 主动失效消息缓存，避免撤回后仍返回未删除的旧快照
        messageCache.Remove(MessageCacheKey(new MessageKey(groupId, messageId)));
        try { await history.MarkMessageAsDeletedAsync(messageId, groupId); }
        catch (Exception ex) { logger.Warn(ex, "标记撤回消息失败: {0}", messageId); }
    }

    private static ProcessedMessage CreateSnapshot(LiteDB.ObjectId id, long groupId, long messageId, long senderId, string nickname, string card, string role, IReadOnlyList<TypedMessage> chain, DateTime time, bool deleted)
        => new(id, groupId, messageId, senderId, nickname ?? string.Empty, card ?? string.Empty, role ?? string.Empty, CloneChain(chain), time, deleted);

    private static ProcessedMessage CreateSnapshot(long groupId, long messageId, long senderId, string nickname, string card, string role, IReadOnlyList<TypedMessage> chain, DateTime time, bool deleted)
        => CreateSnapshot(LiteDB.ObjectId.NewObjectId(), groupId, messageId, senderId, nickname, card, role, chain, time, deleted);

    private static ProcessedMessage CloneSnapshot(ProcessedMessage source)
        => source with { MessageChain = CloneChain(source.MessageChain) };

    private static ProcessedForwardMessage CloneForward(ProcessedForwardMessage source)
        => source with { Messages = source.Messages.Select(CloneSnapshot).ToList() };

    private static IReadOnlyList<TypedMessage> CloneChain(IReadOnlyList<TypedMessage> source)
        => source.Select(item => item.Clone()).ToList();

    private static StoredMessage ToStoredMessage(ProcessedMessage source)
    {
        var gm = new StoredMessage(source.GroupId, source.SenderId, source.SenderNickname, source.SenderGroupNickname, source.SenderGroupRole, source.MessageId, CloneChain(source.MessageChain).ToList(), source.Time, source.IsDeleted);
        gm.Id = source.Id;
        return gm;
    }

    private ProcessedMessage FromStoredMessage(StoredMessage source)
    {
        var localized = LocalizeStoredChain(source.Messages, source.GroupId);
        foreach (var resource in localized.Resources) resourceDescriptors.TryAdd(resource.LocalUri, resource);
        return CreateSnapshot(source.Id, source.GroupId, source.MessageId, source.SenderId, source.SenderNickname, source.SenderGroupNickname, source.SenderGroupRole, localized.Chain, source.Time, source.IsDeleted);
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

    private static string GetFileExtension(string? name)
    {
        var ext = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        // 去掉 URL 查询串干扰：Path.GetExtension 会把 "?xxx" 当扩展名一部分，手动截断
        var q = ext.IndexOf('?');
        if (q >= 0) ext = ext[..q];
        var hash = ext.IndexOf('#');
        if (hash >= 0) ext = ext[..hash];
        return ext;
    }

    private static string GetContentType(string kind, string? name)
    {
        var extension = GetFileExtension(name);
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
    private static bool TryParseObjectId(string s, out LiteDB.ObjectId result)
    {
        try { result = new LiteDB.ObjectId(s); return true; }
        catch { result = LiteDB.ObjectId.Empty; return false; }
    }
}
