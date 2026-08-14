using LlmBackend;
using LlmClient;

namespace Agent;

/// <summary>
/// 图片路由：主模型具备视觉能力（ImageInput）时，图片以用户消息注入当前对话
/// （通过 ToolSet.OnIterationAdd 回调）；否则调用配置的辅助视觉模型生成文字描述。
/// </summary>
public sealed class VisionRouter
{
    private readonly Client? _visionClient;
    private readonly bool _mainHasVision;
    private readonly string _fallbackPrompt;

    /// <summary>主模型是否具备视觉能力。</summary>
    public bool MainHasVision => _mainHasVision;

    /// <summary>
    /// 是否已配置可用的辅助视觉模型（主模型无视觉时描述图片所需）。
    /// </summary>
    public bool HasVisionFallback => _visionClient != null;

    public VisionRouter(
        bool mainHasVision,
        Client? visionClient,
        string fallbackPrompt = "请详细描述这张图片的内容。")
    {
        _mainHasVision = mainHasVision;
        _visionClient = visionClient;
        _fallbackPrompt = string.IsNullOrWhiteSpace(fallbackPrompt)
            ? "请详细描述这张图片的内容。"
            : fallbackPrompt;
    }

    /// <summary>
    /// 构造携带图片的用户消息（主模型视觉路径使用）。caption 非空时作为文字 part 放在图片之前。
    /// </summary>
    public static Message BuildImageMessage(byte[] imageData, string mimeType, string? caption)
    {
        var parts = new List<MessagePart>();
        if (!string.IsNullOrWhiteSpace(caption))
        {
            parts.Add(new MessagePartText { text = caption });
        }
        parts.Add(new MessagePartImage { image = ToDataUrl(imageData, mimeType) });
        return new Message { role = Role.User, content = parts };
    }

    /// <summary>
    /// 使用辅助视觉模型描述图片，返回文字描述。
    /// 未配置辅助视觉模型时抛出异常，调用方应给出可读错误。
    /// </summary>
    public async Task<string> DescribeImageAsync(
        byte[] imageData,
        string mimeType,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        if (_visionClient == null)
        {
            throw new InvalidOperationException(
                "主模型不具备视觉能力，且未配置辅助视觉模型（vision-llm），无法处理图片。");
        }

        var captionText = string.IsNullOrWhiteSpace(caption) ? string.Empty : $"\n图片说明：{caption}";
        var messages = new List<Message>
        {
            BuildImageMessage(imageData, mimeType, _fallbackPrompt + captionText),
        };
        var (response, _) = await _visionClient.Generate(
            cancellationToken,
            messages,
            systemPrompt: string.Empty,
            new LlmOptions());
        return response.Content ?? "（视觉模型未返回内容）";
    }

    private static string ToDataUrl(byte[] data, string mimeType)
    {
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
        return $"data:{mime};base64,{Convert.ToBase64String(data)}";
    }
}
