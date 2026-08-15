using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmBackend;

/// <summary>
/// OpenAI Responses API (/v1/responses) 后端。与 Chat Completions 的主要差异：
/// messages → input(函数调用输出为独立的 function_call_output 条目)、
/// tools 为扁平结构(type/name/description/parameters)、
/// 响应为 output 数组(message / function_call),system prompt 走顶层 instructions。
/// </summary>
public class ResponsesBackend : Backend
{
    // 超时全部由 LlmOptions 的两段 CTS 控制（首字节 + 总时长），HttpClient 本身不设超时
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        IncludeFields = true,
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string? _defaultModel;

    public ResponsesBackend(string baseUrl, string apiKey, string? defaultModel = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _defaultModel = defaultModel;
    }

    public async Task<(GenerateResponse, TokenUsage)> Generate(
        CancellationToken cancellationToken,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options)
    {
        string model = options.Model ?? _defaultModel
            ?? throw new ArgumentException("模型未指定：请在 LlmOptions.Model 或构造函数 defaultModel 中提供", nameof(options));

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = BuildInput(messages),
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            requestBody["instructions"] = systemPrompt;
        }
        if (options.Temperature != null) requestBody["temperature"] = options.Temperature;
        if (options.MaxTokens != null) requestBody["max_output_tokens"] = options.MaxTokens;
        if (options.ReasoningEffort != null)
        {
            requestBody["reasoning"] = new Dictionary<string, object> { ["effort"] = options.ReasoningEffort };
        }
        if (options.Tools != null) requestBody["tools"] = BuildTools(options.Tools);
        if (options.ExtraBody != null)
        {
            foreach (var (key, value) in options.ExtraBody)
            {
                requestBody[key] = value;
            }
        }

        string jsonData = JsonSerializer.Serialize(requestBody, RequestJsonOptions);

        string responseBody;
        // 两段超时：发送到响应头（首字节）由 ttfbCts 控制，响应体读取受 totalCts 总时长约束；
        // 超时映射为不可重试的 RequestTimeoutException，避免 LLM 非幂等请求超时重试造成双倍计费
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.TotalGeneration);
        using var ttfbCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        ttfbCts.CancelAfter(options.TimeToFirstByte ?? LlmDefaults.TimeToFirstByte);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ttfbCts.Token);
            responseBody = await response.Content.ReadAsStringAsync(totalCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw BackendErrors.Map(responseBody, response.StatusCode, response.Headers.RetryAfter?.Delta);
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RequestTimeoutException("Responses API 请求超时", e);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkException($"Responses API 网络错误: {e.Message}", e);
        }

        var json = JsonSerializer.Deserialize<ResponsesResponse>(responseBody)
            ?? throw new InvalidResponseException($"Responses API 返回了无法解析的响应: {BackendErrors.Shorten(responseBody)}");
        if (json.Output == null)
        {
            throw new InvalidResponseException($"Responses API 返回空 output: {BackendErrors.Shorten(responseBody)}");
        }

        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        foreach (var item in json.Output)
        {
            switch (item.Type)
            {
                case "message":
                    foreach (var part in item.Content ?? [])
                    {
                        if (part is { Type: "output_text" } && !string.IsNullOrEmpty(part.Text))
                        {
                            textBuilder.Append(part.Text);
                        }
                    }
                    break;
                case "function_call":
                    toolCalls.Add(new ToolCall(item.CallId ?? "", item.Name ?? "", item.Arguments ?? ""));
                    break;
                case "reasoning":
                    // 深度思考摘要：对齐 ChatCompletionBackend 的 reasoning_content 行为
                    foreach (var summary in item.Summary ?? [])
                    {
                        if (summary is { Type: "summary_text" } && !string.IsNullOrEmpty(summary.Text))
                        {
                            reasoningBuilder.Append(summary.Text);
                        }
                    }
                    break;
                default:
                    // 未知输出类型不静默：记录便于排查，条目本身跳过
                    Console.Error.WriteLine($"[LlmBackend] Responses API 未知输出类型: {item.Type}");
                    break;
            }
        }

        var result = new GenerateResponse(
            textBuilder.Length > 0 ? textBuilder.ToString() : null,
            toolCalls.Count > 0 ? [.. toolCalls] : null,
            reasoningContent: reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null);
        var usage = json.Usage ?? new ResponsesUsage();
        return (result, new TokenUsage(usage.TotalTokens, usage.InputTokens, usage.OutputTokens, usage.CachedTokens));
    }

    /// <summary>构造 input 数组：user/assistant 消息 + function_call_output 工具结果条目。</summary>
    private static List<object> BuildInput(IList<Message> messages)
    {
        var input = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            switch (message.role.Value)
            {
                case "tool":
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.toolCallId,
                        ["output"] = ExtractText(message.content),
                    });
                    break;
                case "assistant":
                    var assistant = new Dictionary<string, object>
                    {
                        ["role"] = "assistant",
                        ["content"] = BuildContent(message.content, imageType: "input_image", textType: "output_text"),
                    };
                    if (message.toolCalls?.Any() == true)
                    {
                        assistant["tool_calls"] = message.toolCalls.Select(call => (object)new Dictionary<string, object>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments,
                        }).ToList();
                    }
                    input.Add(assistant);
                    break;
                default:
                    input.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = BuildContent(message.content, imageType: "input_image"),
                    });
                    break;
            }
        }
        return input;
    }

    /// <summary>Responses 内容块：文本 input_text / output_text，图片 input_image(image_url 传 data URL)。</summary>
    private static List<object> BuildContent(IEnumerable<MessagePart>? parts, string imageType, string textType = "input_text")
    {
        var list = parts?.ToList() ?? [];
        if (list.Count == 0)
        {
            return [];
        }
        return list.Select(part => part switch
        {
            MessagePartText t => (object)new Dictionary<string, object>
            {
                ["type"] = textType,
                ["text"] = t.text ?? string.Empty,
            },
            MessagePartImage img => new Dictionary<string, object>
            {
                ["type"] = imageType,
                ["image_url"] = img.image,
            },
            _ => new Dictionary<string, object> { ["type"] = textType, ["text"] = string.Empty },
        }).ToList();
    }

    /// <summary>Responses 工具为扁平结构，与 Chat Completions 的 {type,function:{...}} 不同。</summary>
    private static List<object> BuildTools(IEnumerable<ToolDef> tools)
        => tools.Select(tool => (object)new Dictionary<string, object>
        {
            ["type"] = "function",
            ["name"] = tool.function.name,
            ["description"] = tool.function.description,
            ["parameters"] = (object?)tool.function.parameters ?? new Dictionary<string, object>(),
        }).ToList();

    private static string ExtractText(IEnumerable<MessagePart>? parts)
        => string.Concat((parts ?? []).OfType<MessagePartText>().Select(t => t.text ?? string.Empty));
}

#pragma warning disable CS8618 // 响应 DTO，非空字段由 JSON 反序列化填充
internal class ResponsesResponse
{
    [JsonPropertyName("output")]
    public List<ResponsesOutputItem>? Output { get; set; }

    [JsonPropertyName("usage")]
    public ResponsesUsage? Usage { get; set; }
}

internal class ResponsesOutputItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("content")]
    public List<ResponsesContentPart>? Content { get; set; }

    /// <summary>reasoning 条目的摘要数组（元素为 summary_text）</summary>
    [JsonPropertyName("summary")]
    public List<ResponsesContentPart>? Summary { get; set; }
}

internal class ResponsesContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal class ResponsesUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("input_tokens_details")]
    public ResponsesInputTokensDetails? InputTokensDetails { get; set; }

    [JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;

    [JsonIgnore]
    public int CachedTokens => InputTokensDetails?.CachedTokens ?? 0;
}

internal class ResponsesInputTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }
}
#pragma warning restore CS8618
