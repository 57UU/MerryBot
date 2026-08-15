using LlmBackend;
using LlmClient;

namespace Agent;

/// <summary>
/// 图片路由：主模型具备视觉能力（ImageInput）时，图片以用户消息注入当前对话
/// （通过 ToolSet.InvokeAsync 的调用级回调）；否则按配置顺序逐个调用辅助视觉模型
/// 生成文字描述，当前模型失效（请求异常）时自动降级到下一个（逐层 fallback）。
/// </summary>
public sealed class VisionRouter
{
    private readonly IReadOnlyList<Client> _visionClients;
    private readonly bool _mainHasVision;
    private readonly string _fallbackPrompt;

    /// <summary>主模型是否具备视觉能力。</summary>
    public bool MainHasVision => _mainHasVision;

    /// <summary>是否已配置可用的辅助视觉模型（主模型无视觉时描述图片所需）。</summary>
    public bool HasVisionFallback => _visionClients.Count > 0;

    public VisionRouter(
        bool mainHasVision,
        IReadOnlyList<Client>? visionClients,
        string fallbackPrompt = "请详细描述这张图片的内容。")
    {
        _mainHasVision = mainHasVision;
        _visionClients = visionClients ?? [];
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
        parts.Add(new MessagePartImage { image = MimeTypes.ToDataUrl(imageData, mimeType) });
        return new Message { role = Role.User, content = parts };
    }

    /// <summary>
    /// 使用辅助视觉模型描述图片，返回文字描述。
    /// 按配置顺序逐个尝试；某个模型请求失败时自动切换到下一个；
    /// 全部失败时抛出异常（汇总各模型错误）。未配置任何辅助视觉模型时直接抛出。
    /// </summary>
    public async Task<string> DescribeImageAsync(
        byte[] imageData,
        string mimeType,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        if (_visionClients.Count == 0)
        {
            throw new InvalidOperationException(
                "主模型不具备视觉能力，且未配置辅助视觉模型（vision-llm），无法处理图片。");
        }

        var captionText = string.IsNullOrWhiteSpace(caption) ? string.Empty : $"\n图片说明：{caption}";
        var messages = new List<Message>
        {
            BuildImageMessage(imageData, mimeType, _fallbackPrompt + captionText),
        };

        var errors = new List<string>();
        for (var i = 0; i < _visionClients.Count; i++)
        {
            try
            {
                var (response, _) = await _visionClients[i].Generate(
                    cancellationToken,
                    messages,
                    systemPrompt: string.Empty,
                    new LlmOptions());
                return response.Content ?? "（视觉模型未返回内容）";
            }
            catch (OperationCanceledException)
            {
                // 取消不降级：直接传播，避免在已取消的请求上继续尝试其他模型
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"视觉模型[{i + 1}]: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"所有辅助视觉模型均失败（共 {errors.Count} 个）: {string.Join("；", errors)}");
    }
}
