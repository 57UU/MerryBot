using System.ComponentModel;
using Agent;
using BrowserService;
using CommonLib;
using LlmBackend;
using NapcatClient.Action;
using NapcatClient.MessageType;

namespace BotPlugin;

/// <summary>
/// 消息工具集：读取本地消息引用，或在用户明确要求时将 Markdown 渲染为图片发送到当前群。
/// </summary>
public class MessageTool : ToolSet
{
    /// <summary>工具输出最大长度（字符），避免撑爆上下文</summary>
    private const int MaxOutputLength = 6000;
    private const int MaxMarkdownLength = 30_000;
    private static readonly HttpClient ImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly IMessageService messageService;
    private readonly MessageChannel channel;
    private readonly Browser browser;
    private readonly SessionKey session;
    private readonly long groupId;
    private readonly VisionRouter visionRouter;
    /// <summary>图片下载大小上限（字节），防止超大图片撑爆上下文</summary>
    private readonly int maxImageBytes;
    private readonly ToolSetBridge bridge;
    private readonly ISimpleLogger _logger;
    private readonly AutoChatSettings? autoChatSettings;

    public MessageTool(IMessageService messageService, MessageChannel channel, Browser browser, SessionKey session, VisionRouter visionRouter, int maxImageBytes, ISimpleLogger? logger = null, AutoChatSettings? autoChat = null)
    {
        this.messageService = messageService;
        this.channel = channel;
        this.browser = browser;
        this.session = session;
        this.groupId = long.Parse(session.Id);
        this.visionRouter = visionRouter;
        this.maxImageBytes = maxImageBytes;
        _logger = logger ?? SimpleLog.Default;
        autoChatSettings = autoChat;

        var builder = new ToolSetBridge.Builder();
        builder.AddFunction<MessageArgs>(
            "get_message",
            "获取消息的完整内容（推荐传入 messageKey）",
            args => GetMessageAsync(args.messageKey ?? args.messageUrl));
        builder.AddFunction<GetGroupContextArgs>("get_group_context", "分页获取当前历史消息上下文（按 Time 倒序，最新在前；首次传空取最新，翻更早时传入上一页返回的 lastMessageKey 或 lastMessageId）", args => GetGroupContextAsync(args));
        // 主模型与辅助视觉模型均不可用时没有图片查看能力，不注册 load_image
        if (visionRouter.MainHasVision || visionRouter.HasVisionFallback)
        {
            builder.AddFunction<GetMessageImageArgs>("load_image", "获取对话中的图片并查看。", GetMessageImageAsync);
        }
        builder.AddFunction<MarkdownMessageArgs>("send_markdown", "以MD格式发送文本", args => SendMarkdownMessage(args.markdown));
        // 自动水群模式才注册 send_message：非 auto 会话保持原有行为（最终回复自动发送），不新增发送口
        if (autoChat != null)
        {
            builder.AddFunction<SendMessageArgs>("send_message", "向当前群发送一条文本消息（自动水群模式下唯一的发送口；不调用则本轮不回复）", SendMessageAsync);
        }
        bridge = builder.Build();
    }

    /// <summary>工具参数：消息引用（优先 ObjectId）。</summary>
    private sealed class MessageArgs
    {
        [Description("消息Id，24位 ObjectId（推荐，从历史上下文 key 复制）；也兼容 merrybot://message/... 或 merrybot://forward/...")]
        public string messageUrl { get; set; } = string.Empty;

        [Description("同 messageUrl，推荐用 ObjectId，与 messageUrl 二选一")]
        public string? messageKey { get; set; }
    }

    private sealed class GetMessageImageArgs
    {
        [Description("图片引用地址")]
        public string image { get; set; } = string.Empty;
    }

    /// <summary>群聊上下文游标分页参数：按 Time 倒序，最新在前；优先用 ObjectId 翻页。</summary>
    private sealed class GetGroupContextArgs
    {
        [Description("锚点消息ID（兼容旧调用），传上一页返回的 lastMessageId 来获取更早的消息；首次获取传空或0取最新")]
        public long? beforeMessageId { get; set; }

        [Description("锚点 Id（推荐，24位 ObjectId），传上一页返回的 lastMessageKey 来获取更早的消息；与 beforeMessageId 二选一，优先使用本字段")]
        public string? beforeMessageKey { get; set; }

        [Description("每页消息条数，默认 20，范围 1-50")]
        public int pageSize { get; set; } = 20;
    }

    private sealed class MarkdownMessageArgs
    {
        [Description("markdown内容")]
        public string markdown { get; set; } = string.Empty;
    }

    private sealed class SendMessageArgs
    {
        [Description("要发送到当前群的文本内容")]
        public string text { get; set; } = string.Empty;
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
        => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>
    /// 通过 ObjectId 或本地消息引用读取普通消息或合并转发消息的完整内容。
    /// </summary>
    private async Task<string> GetMessageAsync(string messageId)
    {
        var reference = messageId?.Trim() ?? string.Empty;
        if (TryParseObjectId(reference, out _))
        {
            var byKey = await messageService.GetMessageByObjectIdAsync(reference);
            if (byKey != null) return FormatMessage(byKey);
            return $"未找到消息: {reference}（Id 不存在或不在当前群）";
        }

        var isMessage = LocalMessageReference.TryParseMessage(reference, out _, out _);
        var isForward = LocalMessageReference.TryParseForward(reference, out _);
        if (!isMessage && !isForward)
        {
            throw new ArgumentException(
                $"消息引用格式错误：必须填写消息 Id（24位 ObjectId）或 merrybot://message/... / merrybot://forward/... 内部引用，不能使用裸 ID 或外部 URL。收到：{reference}",
                nameof(messageId));
        }

        if (isMessage)
        {
            var message = await messageService.GetMessageAsync(groupId, reference);
            if (message == null)
            {
                return $"未找到消息: {reference}（可能已未记录或不在当前群）";
            }
            return FormatMessage(message);
        }

        var entry = await messageService.GetForwardAsync(reference, groupId);
        if (entry == null || entry.Messages.Count == 0)
        {
            return $"未找到转发消息: {reference}";
        }
        return Cap(string.Join("\n", entry.Messages.Select(FormatMessage)));
    }

    /// <summary>
    /// 游标分页获取群聊历史：按 Time 倒序，最新在前；优先用 messageKey 翻页。
    /// 返回体末尾附带 lastMessageKey / lastMessageId，供下次翻页使用。
    /// </summary>
    private async Task<string> GetGroupContextAsync(GetGroupContextArgs args)
    {
        var pageSize = Math.Clamp(args.pageSize, 1, 50);
        IReadOnlyList<ProcessedMessage> messages;
        string anchorInfo;
        if (!string.IsNullOrWhiteSpace(args.beforeMessageKey) && TryParseObjectId(args.beforeMessageKey, out _))
        {
            messages = await messageService.GetGroupMessagesBeforeKeyAsync(groupId, args.beforeMessageKey, pageSize);
            anchorInfo = $"beforeMessageKey={args.beforeMessageKey}";
        }
        else
        {
            var before = args.beforeMessageId.HasValue && args.beforeMessageId.Value != 0 ? args.beforeMessageId.Value : (long?)null;
            messages = await messageService.GetGroupMessagesBeforeAsync(groupId, before, pageSize);
            anchorInfo = before.HasValue ? $"beforeMessageId={before.Value}" : "beforeMessageId=null(最新)";
        }
        var total = await messageService.GetGroupMessageCountAsync(groupId);
        if (messages.Count == 0)
        {
            return total == 0
                ? "当前群暂无历史消息。"
                : $"没有更多历史消息了（共 {total} 条，{anchorInfo}）。";
        }

        var body = string.Join("\n", messages.Select(FormatMessage));
        var lastMessageId = messages[^1].MessageId;
        var lastMessageKey = messages[^1].Id.ToString();
        var anchor = anchorInfo;
        return Cap($"群聊历史消息（共 {total} 条，本页 {messages.Count} 条，{anchor}，lastMessageId={lastMessageId}，lastMessageKey={lastMessageKey}）：\n{body}\n\n[翻页提示] 下次取更早消息请传 beforeMessageKey={lastMessageKey}（或 beforeMessageId={lastMessageId} 兼容）");
    }

    /// <summary>按图片引用加载并查看图片：主模型有视觉能力时通过调用级回调把图片注入对话，</summary>
    /// 否则调用辅助视觉模型生成文字描述。引用来自消息文本中显示的 image 标识
    /// （pipeline 已把图片 Url/File 改写为 merrybot://resource/image/{hash} 本地引用）。
    /// </summary>
    private async Task<string> GetMessageImageAsync(GetMessageImageArgs args, CancellationToken cancellationToken, Action<Message> onIterationAdd)
    {
        var reference = args.image?.Trim() ?? string.Empty;
        if (reference.Length == 0)
        {
            return "image 参数不能为空：请传入消息文本中显示的图片引用（如 merrybot://resource/image/xxx 或 http(s):// 图片地址）。";
        }

        var (data, contentType) = await LoadImageByReferenceAsync(reference, cancellationToken);
        if (data == null || data.Length == 0)
        {
            return $"无法读取图片: {reference}（引用无效或资源不存在）";
        }
        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = MimeTypes.GuessImageContentType(reference) ?? "image/png";
        }

        var caption = $"对话图片: {reference}";
        if (visionRouter.MainHasVision)
        {
            onIterationAdd(VisionRouter.BuildImageMessage(data, contentType, caption));
            return $"已加载图片 {reference} 并注入对话，请直接查看图片内容。";
        }

        if (!visionRouter.HasVisionFallback)
        {
            return "主模型不具备视觉能力，且未配置辅助视觉模型（vision-llm），无法查看图片。";
        }
        var description = await visionRouter.DescribeImageAsync(data, contentType, reference, cancellationToken);
        return $"图片描述（{reference}）：{description}";
    }

    /// <summary>按引用解析图片数据：本地资源 → 本地库；http(s) → 下载（预检+限流）；base64:// → 解码。</summary>
    private async Task<(byte[]? Data, string? ContentType)> LoadImageByReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        if (LocalMessageReference.IsResource(reference))
        {
            var resource = await messageService.GetResourceAsync(reference);
            return resource == null ? (null, null) : (resource.Data, resource.ContentType);
        }

        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await DownloadImageAsync(reference, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消继续传播（会话取消/工具超时），不当作下载失败吞掉
            }
            catch (Exception e)
            {
                _logger.Warn($"下载图片失败: {reference}: {e.Message}");
                return (null, null);
            }
        }

        if (reference.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(reference["base64://".Length..]);
                return (bytes, "image/png");
            }
            catch (FormatException)
            {
                return (null, null);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// 下载图片：Content-Length 预检 + 流式读取累计上限，超限立即中断。
    /// </summary>
    private async Task<(byte[]? Data, string? ContentType)> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn($"下载图片失败: {url}: HTTP {(int)response.StatusCode}");
            return (null, null);
        }

        // Content-Length 预检：声明超限直接拒绝，不下载
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxImageBytes)
        {
            _logger.Warn($"下载图片失败: {url}: Content-Length {declaredLength} 超过上限 {maxImageBytes}");
            return (null, null);
        }

        // 流式读取累计上限：实际大小超限立即中断，防止压缩炸弹/无 Content-Length 的超大响应
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > maxImageBytes)
            {
                _logger.Warn($"下载图片失败: {url}: 实际大小超过上限 {maxImageBytes}，已中断");
                return (null, null);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>与群历史上下文相同的消息渲染格式：[时间] [用户 id(昵称:name)] [key=...]: 内容（key 供 get_message 传入）</summary>
    private static string FormatMessage(ProcessedMessage m) => MessageUtils.FormatFullMessage(m, includeKey: true);
    private async Task<string> SendMarkdownMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Markdown 内容不能为空。", nameof(message));
        }
        if (message.Length > MaxMarkdownLength)
        {
            throw new ArgumentOutOfRangeException(nameof(message), $"Markdown 内容不能超过 {MaxMarkdownLength} 个字符。");
        }

        var image = await browser.TakeMarkdownScreenshot(message);
        await channel.SendMessage(session, [ImageData.FromBinary(image)]);
        return "Markdown 已渲染为图片并发送到当前群。";
    }

    /// <summary>
    /// 自动水群模式的唯一发送口：配额由 AutoChatSendBudget 按轮次控制，超限返回 error 供模型自纠；
    /// DryRun 开启时只记日志不真正发群。
    /// </summary>
    private async Task<string> SendMessageAsync(SendMessageArgs args)
    {
        if (autoChatSettings == null)
        {
            throw new InvalidOperationException("send_message 仅在自动水群模式下可用。");
        }
        if (string.IsNullOrWhiteSpace(args.text))
        {
            throw new ArgumentException("发送内容不能为空。", nameof(args));
        }
        if (!autoChatSettings.Budget.TryAcquire())
        {
            return "{\"error\": \"本轮发送次数已达上限，请停止调用 send_message\"}";
        }
        if (autoChatSettings.DryRun)
        {
            string preview = args.text.Length > 200 ? args.text[..200] + "…" : args.text;
            _logger.Info($"[AutoChat] 模拟发送（群 {groupId}）: {preview}");
            return "已模拟发送（DryRun 开启，未真正发群）。";
        }
        await channel.SendMessage(session, [TextData.FromText(args.text)]);
        return "已发送到当前群。";
    }

    private static string Cap(string text) =>
        text.Length <= MaxOutputLength
            ? text
            : text[..MaxOutputLength] + $"\n…（内容过长已截断，全文共 {text.Length} 字符）";

    private static bool TryParseObjectId(string s, out LiteDB.ObjectId result)
    {
        try { result = new LiteDB.ObjectId(s); return true; }
        catch { result = LiteDB.ObjectId.Empty; return false; }
    }
}
