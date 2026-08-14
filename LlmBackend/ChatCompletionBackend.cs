using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmBackend;

/// <summary>
/// OpenAI 兼容 /chat/completions 接口后端，支持文本、图片、工具调用与 reasoning 内容
/// </summary>
public class ChatCompletionBackend : Backend
{
    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim _semaphore = new(5, 5);
    // ToolDef and FunctionDef intentionally expose OpenAI-shaped public fields
    // (type/function/name/parameters). Include fields when serializing a request
    // so providers receive the required top-level `type: "function"` member.
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        IncludeFields = true,
    };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string? _defaultModel;

    public ChatCompletionBackend(string baseUrl, string apiKey, string? defaultModel = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _defaultModel = defaultModel;
    }

    public async Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
    {
        string model = options.Model ?? _defaultModel
            ?? throw new ArgumentException("模型未指定：请在 LlmOptions.Model 或构造函数 defaultModel 中提供", nameof(options));

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, systemPrompt),
        };
        if (options.Temperature != null) requestBody["temperature"] = options.Temperature;
        if (options.ReasoningEffort != null) requestBody["reasoning_effort"] = options.ReasoningEffort;
        if (options.MaxTokens != null) requestBody["max_tokens"] = options.MaxTokens;
        if (options.Tools != null) requestBody["tools"] = options.Tools;
        if (options.ExtraBody != null)
        {
            foreach (var (key, value) in options.ExtraBody)
            {
                requestBody[key] = value;
            }
        }

        string jsonData = JsonSerializer.Serialize(requestBody, RequestJsonOptions);

        await _semaphore.WaitAsync(cancellationToken);
        string responseBody;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            try
            {
                using var response = await Client.SendAsync(request, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw BuildLlmException(response.StatusCode, responseBody, response.Headers.RetryAfter?.Delta);
                }
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NetworkException("ChatCompletion 请求超时", e);
            }
            catch (HttpRequestException e)
            {
                throw new NetworkException($"ChatCompletion 网络错误: {e.Message}", e);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        var json = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody)
            ?? throw new HttpRequestException($"ChatCompletion API 返回了无法解析的响应: {responseBody}");

        if (json.Choices == null || json.Choices.Count == 0)
        {
            throw new HttpRequestException($"ChatCompletion API 返回空 choices: {responseBody}");
        }
        var message = json.Choices[0].Message
            ?? throw new HttpRequestException($"ChatCompletion API 返回空 message: {responseBody}");

        var toolCalls = message.ToolCalls?
            .Where(t => t.Function != null)
            .Select(t => new ToolCall(t.Id ?? "", t.Function?.Name ?? "", t.Function?.Arguments ?? ""))
            .ToArray();
        if (toolCalls is { Length: 0 }) toolCalls = null;

        var result = new GenerateResponse(ExtractContent(message.Content), toolCalls, message.ReasoningContent);
        var usage = json.Usage ?? new Usage();
        return (result, new TokenUsage(usage.TotalTokens, usage.PromptTokens, usage.CompletionTokens, usage.CachedTokens));
    }

    /// <summary>
    /// 按 HTTP 状态码映射为标准 LlmException，供调用方判断是否可重试
    /// </summary>
    private static LlmException BuildLlmException(HttpStatusCode statusCode, string responseBody, TimeSpan? retryAfter)
        => BackendErrors.Map(responseBody, statusCode, retryAfter);

    /// <summary>
    /// 合并 systemPrompt 与消息列表：systemPrompt 非空时作为首条系统消息，
    /// 并覆盖消息列表中原有的首条系统消息，避免重复
    /// </summary>
    private static List<object> BuildMessages(IList<Message> messages, string systemPrompt)
    {
        bool hasSystemPrompt = !string.IsNullOrWhiteSpace(systemPrompt);
        var result = new List<object>(messages.Count + (hasSystemPrompt ? 1 : 0));

        if (hasSystemPrompt)
        {
            result.Add(new Dictionary<string, object>
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            });
        }

        for (int i = 0; i < messages.Count; i++)
        {
            if (i == 0 && hasSystemPrompt && messages[0].role == Role.System)
            {
                continue;
            }
            result.Add(ConvertMessage(messages[i]));
        }
        return result;
    }

    /// <summary>
    /// 转换一条对话消息为请求体中的消息对象
    /// </summary>
    private static object ConvertMessage(Message message)
    {
        var result = new Dictionary<string, object>
        {
            ["role"] = message.role.Value,
            ["content"] = ConvertContent(message.content),
        };

        // assistant：工具调用
        if (message.toolCalls != null && message.toolCalls.Any())
        {
            result["tool_calls"] = message.toolCalls.Select(t => (object)new Dictionary<string, object>
            {
                ["id"] = t.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["arguments"] = t.Arguments,
                },
            }).ToList();
        }
        // assistant：reasoning 内容
        if (!string.IsNullOrEmpty(message.reasoningContent))
        {
            result["reasoning_content"] = message.reasoningContent;
        }
        // tool：对应的 tool_call_id
        if (!string.IsNullOrEmpty(message.toolCallId))
        {
            result["tool_call_id"] = message.toolCallId;
        }
        return result;
    }

    /// <summary>
    /// 转换消息内容：仅一个文本 part 时输出字符串，含图片或多个 part 时输出数组
    /// </summary>
    private static object ConvertContent(IEnumerable<MessagePart>? parts)
    {
        var list = parts?.ToList() ?? new List<MessagePart>();
        if (list.Count == 0)
        {
            return string.Empty;
        }
        if (list.Count == 1 && list[0] is MessagePartText text)
        {
            return text.text ?? string.Empty;
        }
        return list.Select(part => part switch
        {
            MessagePartText t => (object)new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = t.text ?? string.Empty,
            },
            MessagePartImage img => new Dictionary<string, object>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object> { ["url"] = img.image },
            },
            _ => new Dictionary<string, object> { ["type"] = "text", ["text"] = string.Empty },
        }).ToList();
    }

    /// <summary>
    /// 提取响应内容：兼容字符串与 parts 数组两种形式
    /// </summary>
    private static string? ExtractContent(object? content)
    {
        if (content is not JsonElement element)
        {
            return content as string;
        }
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => string.Concat(element.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(p => p.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "")),
            _ => null,
        };
    }
}

#pragma warning disable CS8618 // 响应 DTO，非空字段由 JSON 反序列化填充
internal class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }
}

internal class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}

internal class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public object? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ResponseToolCall>? ToolCalls { get; set; }
}

internal class ResponseToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public ResponseFunction? Function { get; set; }
}

internal class ResponseFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    // DeepSeek：prompt_cache_hit_tokens
    [JsonPropertyName("prompt_cache_hit_tokens")]
    public int PromptCacheHitTokens { get; set; }

    // OpenAI：prompt_tokens_details.cached_tokens
    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokensDetails? PromptTokensDetails { get; set; }

    /// <summary>
    /// 缓存命中的 prompt token 数：优先 OpenAI 的 prompt_tokens_details.cached_tokens，
    /// 其次 DeepSeek 的 prompt_cache_hit_tokens，两者都无则取 0
    /// </summary>
    [JsonIgnore]
    public int CachedTokens =>
        PromptTokensDetails?.CachedTokens ?? PromptCacheHitTokens;
}

internal class PromptTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }
}
#pragma warning restore CS8618
