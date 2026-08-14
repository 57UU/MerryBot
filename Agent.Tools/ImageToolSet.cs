using LlmBackend;
using LlmClient;
using System.Text.Json;

namespace Agent.Tools;

/// <summary>
/// 图片工具：通过 load_image 工具加载图片。
/// - 多模态模式（构造时不传解释器）：图片引用直接透传，通过基类回调 OnIterationAdd
///   以用户消息加入对话，模型在下一轮即可看到图片内容。
/// - 解释器模式（构造时传 Client 解释器）：把图片发给外挂图片解释器分析，
///   返回解释器的文本描述作为工具结果；该模式工具参数多一个可选的 attention（注意点）。
/// 图片获取由本类自行处理（当前为引用透传，URL / data URL 均可）；调用方根据
/// 主模型模态与解释器配置决定构造哪种模式，两者皆无时不注册该工具。
/// </summary>
public class ImageToolSet : ToolSet
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Client? interpreter; // null → 多模态模式；非 null → 解释器模式
    private readonly ToolDef def;

    public ImageToolSet(Client? interpreter = null)
    {
        this.interpreter = interpreter;
        bool withAttention = interpreter != null;
        def = new ToolDef
        {
            type = "function",
            function = new FunctionDef
            {
                name = "load_image",
                description = withAttention
                    ? "加载图片并调用图片解释器分析图片内容，返回对图片的描述。"
                    : "加载图片，加载完成后可在下一轮对话中直接查看图片内容。",
                parameters = JsonSerializer.SerializeToElement(BuildSchema(withAttention)),
            },
        };
    }

    /// <summary>工具参数：image 为图片 URL 或 data URL；attention 仅解释器模式使用，可选</summary>
    private sealed class ImageArgs
    {
        public string image { get; set; } = string.Empty;
        public string? attention { get; set; }
    }

    private static Dictionary<string, object?> BuildSchema(bool withAttention)
    {
        var properties = new Dictionary<string, object?>
        {
            ["image"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "图片 URL 或 data URL",
            },
        };
        if (withAttention)
        {
            properties["attention"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "需要重点注意的内容，可选",
            };
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new List<string> { "image" },
        };
    }

    public override IList<ToolDef> Tools() => [def];

    public override string? Prompt() => interpreter != null
        ? "如需分析图片，调用 load_image 工具，将返回图片解释器对图片的描述。"
        : "如需查看图片，调用 load_image 工具，加载完成后下一轮对话即可看到图片内容。";

    public override async Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var args = string.IsNullOrWhiteSpace(toolCall.Arguments) ? "{}" : toolCall.Arguments;
            var parsed = JsonSerializer.Deserialize<ImageArgs>(args, DeserializeOptions)
                ?? throw new ArgumentException("参数解析失败");
            if (string.IsNullOrWhiteSpace(parsed.image))
            {
                throw new ArgumentException("image 参数不能为空");
            }

            if (interpreter != null)
            {
                return await InterpretAsync(cancellationToken, parsed.image, parsed.attention);
            }

            // 多模态模式：图片以用户消息加入对话，下一轮生成时模型可见
            OnIterationAdd?.Invoke(new Message
            {
                role = Role.User,
                content = [new MessagePartImage { image = parsed.image }],
            });
            return "图片已加载，将在下一轮对话中展示";
        }
        catch (Exception e)
        {
            // 参数解析或执行失败时返回错误信息，便于模型自行纠正后重试
            return $"{{\"error\": {JsonSerializer.Serialize(e.Message)}}}";
        }
    }

    private async Task<string> InterpretAsync(CancellationToken cancellationToken, string image, string? attention)
    {
        var parts = new List<MessagePart>();
        if (!string.IsNullOrWhiteSpace(attention))
        {
            parts.Add(new MessagePartText { text = $"请重点观察以下内容：{attention}" });
        }
        parts.Add(new MessagePartImage { image = image });

        var (response, _) = await interpreter!.Generate(
            cancellationToken,
            [new Message { role = Role.User, content = parts }],
            string.Empty,
            new LlmOptions());
        return response.Content ?? "{\"error\": \"图片解释器未返回内容\"}";
    }
}
