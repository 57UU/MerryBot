using System.ComponentModel;
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
            "当消息内容中包含图片、需要查看图片时，使用 get_message_image，把消息文本中显示的图片引用（image merrybot://... 或 image http(s)://... 后面的引用）原样传入。仅当用户明确要求向当前群发送 Markdown 图片时，使用 send_markdown_message。");
        builder.AddFunction<MessageArgs>("get_forward_message", "获取合并转发消息的完整内容（含每条消息的发送者、时间与内容）", args => GetForwardMessage(args.messageId));
        builder.AddFunction<MessageArgs>("get_reply_message", "获取被回复消息的完整内容（发送者、时间与内容）", args => GetReplyMessage(args.messageId));
        builder.AddFunction<GetMessageImageArgs>("get_message_image", "获取对话中的图片并查看。传入消息文本中显示的图片引用（image 后跟的 merrybot:// 本地引用或 http(s):// 地址）。主模型有视觉能力时直接查看原图，否则会用辅助视觉模型描述图片内容。", args => GetMessageImageAsync(args));
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
        [Description("图片引用：get_reply_message / get_forward_message 内容中显示的图片标识（如 merrybot://resource/image/xxx 或 http(s):// 图片地址），原样传入即可")]
        public string image { get; set; } = string.Empty;
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
    /// 按图片引用加载并查看图片：主模型有视觉能力时通过 OnIterationAdd 把图片注入对话，
    /// 否则调用辅助视觉模型生成文字描述。引用来自消息文本中显示的 image 标识
    /// （pipeline 已把图片 Url/File 改写为 merrybot://resource/image/{hash} 本地引用）。
    /// </summary>
    private async Task<string> GetMessageImageAsync(GetMessageImageArgs args)
    {
        var reference = args.image?.Trim() ?? string.Empty;
        if (reference.Length == 0)
        {
            return "image 参数不能为空：请传入消息文本中显示的图片引用（如 merrybot://resource/image/xxx 或 http(s):// 图片地址）。";
        }

        var (data, contentType) = await LoadImageByReferenceAsync(reference);
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
            var add = OnIterationAdd;
            if (add == null)
            {
                return "当前无法把图片注入对话（回调不可用），请稍后重试。";
            }
            add(VisionRouter.BuildImageMessage(data, contentType, caption));
            return $"已加载图片 {reference} 并注入对话，请直接查看图片内容。";
        }

        if (!visionRouter.HasVisionFallback)
        {
            return "主模型不具备视觉能力，且未配置辅助视觉模型（vision-llm），无法查看图片。";
        }
        var description = await visionRouter.DescribeImageAsync(data, contentType, reference, CancellationToken.None);
        return $"图片描述（{reference}）：{description}";
    }

    /// <summary>按引用解析图片数据：本地资源 → 本地库；http(s) → 下载；base64:// → 解码。</summary>
    private async Task<(byte[]? Data, string? ContentType)> LoadImageByReferenceAsync(string reference)
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
                var bytes = await ImageHttpClient.GetByteArrayAsync(reference);
                return (bytes, MimeTypes.GuessImageContentType(reference));
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
