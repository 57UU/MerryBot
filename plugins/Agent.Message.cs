using System.ComponentModel;
using System.Text;
using Agent;
using BrowserService;
using CommonLib;
using LlmBackend;
using NapcatClient.Action;
using NapcatClient.MessageType;

namespace BotPlugin;

/// <summary>
/// 消息工具集：读取引用消息，或在用户明确要求时将 Markdown 渲染为图片发送到当前群。
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
    private readonly Actions bot;
    private readonly Browser browser;
    private readonly long groupId;
    private readonly VisionRouter visionRouter;
    private readonly ToolSetBridge bridge;

    public MessageTool(IMessageService messageService, Actions bot, Browser browser, long groupId, VisionRouter visionRouter)
    {
        this.messageService = messageService;
        this.bot = bot;
        this.browser = browser;
        this.groupId = groupId;
        this.visionRouter = visionRouter;

        var builder = new ToolSetBridge.Builder(
            "当消息链中出现合并转发（forward）或回复（reply）引用、需要查看被引用消息的完整内容时，使用 get_forward_message / get_reply_message；" +
            "当被引用消息或对话中包含图片、需要查看图片内容时，使用 get_message_image。仅当用户明确要求向当前群发送 Markdown 图片时，使用 send_markdown_message。");
        builder.AddFunction<MessageArgs>("get_forward_message", "获取合并转发消息的完整内容（含每条消息的发送者、时间与内容）", args => GetForwardMessage(args.messageId));
        builder.AddFunction<MessageArgs>("get_reply_message", "获取被回复消息的完整内容（发送者、时间与内容）", args => GetReplyMessage(args.messageId));
        builder.AddFunction<GetMessageImageArgs>("get_message_image", "获取对话中某条消息的图片并查看（支持被回复/转发消息里的图片）。主模型有视觉能力时直接查看原图，否则会用辅助视觉模型描述图片内容。", args => GetMessageImageAsync(args));
        builder.AddFunction<MarkdownMessageArgs>("send_markdown_message", "将 Markdown（支持 LaTeX、Mermaid）渲染为图片并发送到当前群。仅在用户明确要求发送时调用。", args => SendMarkdownMessage(args.markdown));
        bridge = builder.Build();
    }

    /// <summary>工具参数：消息 ID</summary>
    private sealed class MessageArgs
    {
        [Description("QQ 消息 ID：转发消息填转发 ID，回复消息填被回复消息的 ID")]
        public string messageId { get; set; } = string.Empty;
    }

    private sealed class GetMessageImageArgs
    {
        [Description("包含图片的消息 ID（可用 get_reply_message 返回中的消息 ID）")]
        public string messageId { get; set; } = string.Empty;
    }

    private sealed class MarkdownMessageArgs
    {
        [Description("要渲染并发送的完整 Markdown 内容；支持 LaTeX 和 Mermaid")]
        public string markdown { get; set; } = string.Empty;
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall) => bridge.InvokeAsync(cancellationToken, toolCall);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>
    /// 读取合并转发消息的完整内容。数据来自本地历史库（消息进入时已随资源一起落库）。
    /// </summary>
    private async Task<string> GetForwardMessage(string messageId)
    {
        var entry = await messageService.GetForwardAsync(messageId, groupId);
        if (entry == null || entry.Messages.Count == 0)
        {
            return $"未找到转发消息: {messageId}";
        }
        return Cap(string.Join("\n", entry.Messages.Select(FormatMessage)));
    }

    /// <summary>
    /// 读取被回复消息的完整内容。支持消息 ID 或处理链提供的本地 URI。
    /// </summary>
    private async Task<string> GetReplyMessage(string messageId)
    {
        var message = await messageService.GetReplyAsync(groupId, messageId);
        if (message == null)
        {
            return $"未找到消息: {messageId}（可能已被撤回、未记录或不在当前群）";
        }
        return FormatMessage(message);
    }

    /// <summary>
    /// 加载对话消息中的全部图片：主模型有视觉能力时通过 OnIterationAdd 把图片注入对话，
    /// 否则调用辅助视觉模型逐张生成文字描述。
    /// </summary>
    private async Task<string> GetMessageImageAsync(GetMessageImageArgs args)
    {
        var message = await messageService.GetMessageAsync(groupId, args.messageId);
        if (message == null)
        {
            return $"未找到消息: {args.messageId}（可能已被撤回、未记录或不在当前群）";
        }

        var images = message.MessageChain.OfType<ImageData>().ToList();
        if (images.Count == 0)
        {
            return $"消息 {args.messageId} 不包含图片。";
        }

        var loaded = new List<(byte[] Data, string ContentType)>();
        var failedCount = 0;
        foreach (var image in images)
        {
            var (data, contentType) = await LoadImageAsync(image);
            if (data == null || data.Length == 0)
            {
                failedCount++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = GuessImageContentType(image.File) ?? "image/png";
            }
            loaded.Add((data, contentType));
        }
        if (loaded.Count == 0)
        {
            return "图片数据为空，无法查看。";
        }

        var caption = $"对话图片（消息 {message.MessageId}，共 {images.Count} 张）";
        if (visionRouter.MainHasVision)
        {
            var add = OnIterationAdd;
            if (add == null)
            {
                return "当前无法把图片注入对话（回调不可用），请稍后重试。";
            }
            add(VisionRouter.BuildImageMessage(loaded, caption));
            var failedNote = failedCount > 0 ? $"（{failedCount} 张加载失败）" : string.Empty;
            return $"已加载 {loaded.Count}/{images.Count} 张图片并注入对话{failedNote}，请直接查看图片内容。";
        }

        if (!visionRouter.HasVisionFallback)
        {
            return "主模型不具备视觉能力，且未配置辅助视觉模型（vision-llm），无法查看图片。";
        }
        var sb = new StringBuilder($"消息 {message.MessageId} 共 {images.Count} 张图片：");
        for (var i = 0; i < loaded.Count; i++)
        {
            var description = await visionRouter.DescribeImageAsync(
                loaded[i].Data,
                loaded[i].ContentType,
                i < images.Count ? images[i].Summary : null,
                CancellationToken.None);
            sb.AppendLine($"\n第 {i + 1} 张：{description}");
        }
        if (failedCount > 0)
        {
            sb.AppendLine($"\n（另有 {failedCount} 张图片加载失败）");
        }
        return sb.ToString();
    }

    /// <summary>解析图片数据：本地资源 → 本地库；http(s) → 下载；base64:// → 解码。</summary>
    private async Task<(byte[]? Data, string? ContentType)> LoadImageAsync(ImageData image)
    {
        var reference = image.Url ?? image.File;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, null);
        }

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
                var bytes = await ImageHttpClient.GetByteArrayAsync(reference);
                var contentType = GuessImageContentType(reference);
                return (bytes, contentType);
            }
            catch (Exception e)
            {
                ConsoleLogger.Instance.Warn($"下载图片失败: {reference}: {e.Message}");
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

    private static string? GuessImageContentType(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var lower = reference.ToLowerInvariant();
        return lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.Contains("jpeg") || lower.Contains("jpg")
            ? "image/jpeg"
            : lower.EndsWith(".gif") || lower.Contains("gif")
                ? "image/gif"
                : lower.EndsWith(".webp") || lower.Contains("webp")
                    ? "image/webp"
                    : lower.EndsWith(".png") || lower.Contains("png")
                        ? "image/png"
                        : null;
    }

    /// <summary>与群历史上下文相同的消息渲染格式：[时间] 昵称: 内容</summary>
    private static string FormatMessage(ProcessedMessage m)
    {
        var timeStr = m.Time.ToString("yyyy-MM-dd HH:mm");
        var name = string.IsNullOrEmpty(m.SenderGroupNickname) ? m.SenderNickname : m.SenderGroupNickname;
        var content = string.Join("", m.MessageChain.Select(tm => tm.ToString()));
        return $"[{timeStr}] {name}: {content}";
    }
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
        await bot.SendGroupMessage(groupId, [ImageData.FromBinary(image)]);
        return "Markdown 已渲染为图片并发送到当前群。";
    }

    private static string Cap(string text) =>
        text.Length <= MaxOutputLength
            ? text
            : text[..MaxOutputLength] + $"\n…（内容过长已截断，全文共 {text.Length} 字符）";
}
