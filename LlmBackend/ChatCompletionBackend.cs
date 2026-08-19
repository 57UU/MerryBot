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
    // 超时全部由 LlmOptions 的两段 CTS 控制（首字节 + 总时长），HttpClient 本身不设超时
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
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

        string jsonData = JsonSerializer.Serialize(
            BuildRequestBody(model, messages, systemPrompt, options, stream: false), RequestJsonOptions);

        string responseBody;
        // 非流式请求只挂总时长超时：LLM 服务端"算完整轮才发响应头"，首字节约等于
        // 整个生成耗时，TTFB（首 token 延迟）对非流式无意义且会误杀长生成
        // （默认 60s 远小于默认总时长 5min，深度思考模型首字节常超 1 分钟）；
        // 超时映射为不可重试的 RequestTimeoutException，避免 LLM 非幂等请求超时重试造成双倍计费
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.TotalGeneration);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, totalCts.Token);
            responseBody = await response.Content.ReadAsStringAsync(totalCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw BuildLlmException(response.StatusCode, responseBody, response.Headers.RetryAfter?.Delta);
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RequestTimeoutException("ChatCompletion 请求超时", e);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkException($"ChatCompletion 网络错误: {e.Message}", e);
        }

        var json = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody)
            ?? throw new InvalidResponseException($"ChatCompletion API 返回了无法解析的响应: {BackendErrors.Shorten(responseBody)}");

        if (json.Choices == null || json.Choices.Count == 0)
        {
            throw new InvalidResponseException($"ChatCompletion API 返回空 choices: {BackendErrors.Shorten(responseBody)}");
        }
        var message = json.Choices[0].Message
            ?? throw new InvalidResponseException($"ChatCompletion API 返回空 message: {BackendErrors.Shorten(responseBody)}");

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
    /// 流式生成：SSE 读循环逐帧推送 <see cref="IStreamSink.OnTextDelta"/> /
    /// <see cref="IStreamSink.OnReasoningDelta"/>，工具调用按 index 累积，流读完
    /// 回调 <see cref="IStreamSink.OnCompleted"/>（携带完整响应与用量）。
    /// 两段超时与非流式一致：首字节由 ttfbCts 控制（仅用于 SendAsync），
    /// 整个流读取受 totalCts 总时长约束。中途的网络/超时/解析异常归一化为
    /// LlmException（NetworkException / RequestTimeoutException / InvalidResponseException）。
    /// </summary>
    public async Task GenerateStream(
        IStreamSink sink,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        string model = options.Model ?? _defaultModel
            ?? throw new ArgumentException("模型未指定：请在 LlmOptions.Model 或构造函数 defaultModel 中提供", nameof(options));

        string jsonData = JsonSerializer.Serialize(
            BuildRequestBody(model, messages, systemPrompt, options, stream: true), RequestJsonOptions);

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.StreamingTotalGeneration);
        using var ttfbCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        ttfbCts.CancelAfter(options.TimeToFirstByte ?? LlmDefaults.TimeToFirstByte);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ttfbCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorBody = await response.Content.ReadAsStringAsync(totalCts.Token);
                throw BuildLlmException(response.StatusCode, errorBody, response.Headers.RetryAfter?.Delta);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(totalCts.Token);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            // 工具调用按 delta 携带的 index 累积（id/name/arguments 分片到达）
            var toolCalls = new List<(string Id, string Name, StringBuilder Arguments)>();
            Usage? usage = null;
            string? line;
            while ((line = await reader.ReadLineAsync(totalCts.Token)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }
                var chunk = ParseChunk(data);
                if (chunk.Error != null)
                {
                    throw new InvalidResponseException($"ChatCompletion 流式错误: {BackendErrors.Shorten(chunk.Error)}");
                }
                if (chunk.Done)
                {
                    break;
                }
                if (chunk.Text is { Length: > 0 })
                {
                    textBuilder.Append(chunk.Text);
                    sink.OnTextDelta(chunk.Text);
                }
                if (chunk.Reasoning is { Length: > 0 })
                {
                    reasoningBuilder.Append(chunk.Reasoning);
                    sink.OnReasoningDelta(chunk.Reasoning);
                }
                if (chunk.ToolCallParts is { Count: > 0 })
                {
                    foreach (var (index, id, name, arguments) in chunk.ToolCallParts)
                    {
                        while (toolCalls.Count <= index)
                        {
                            toolCalls.Add(("", "", new StringBuilder()));
                        }
                        var call = toolCalls[index];
                        if (!string.IsNullOrEmpty(id)) call.Id = id;
                        if (!string.IsNullOrEmpty(name)) call.Name = name;
                        if (!string.IsNullOrEmpty(arguments)) call.Arguments.Append(arguments);
                        toolCalls[index] = call;
                    }
                }
                if (chunk.Usage != null)
                {
                    usage = chunk.Usage;
                }
            }

            var resultToolCalls = toolCalls.Count > 0
                ? toolCalls.Select(call => new ToolCall(call.Id, call.Name, call.Arguments.ToString())).ToArray()
                : null;
            var result = new GenerateResponse(
                textBuilder.Length > 0 ? textBuilder.ToString() : null,
                resultToolCalls,
                reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null);
            var tokenUsage = usage is null
                ? TokenUsage.Zero
                : new TokenUsage(usage.TotalTokens, usage.PromptTokens, usage.CompletionTokens, usage.CachedTokens);
            sink.OnCompleted(result, tokenUsage);
        }
        catch (LlmException)
        {
            // 含 sink 回调抛出的 LlmException（如重试层检出正文标记）：不得包装，原样穿透
            throw;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"ChatCompletion 流式请求超时: {e.Message}");
            throw new RequestTimeoutException("ChatCompletion 流式请求超时", e);
        }
        catch (HttpRequestException e)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"ChatCompletion 网络错误: {e.Message}");
            throw new NetworkException($"ChatCompletion 网络错误: {e.Message}", e);
        }
        catch (IOException e)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"ChatCompletion 流读取中断: {e.Message}");
            throw new NetworkException($"ChatCompletion 流读取中断: {e.Message}", e);
        }
    }

    /// <summary>
    /// 构造请求体：非流式与流式共用，流式追加 stream 标志与 include_usage
    /// （让流末尾携带 usage 块，终结事件才能给出完整用量）。
    /// </summary>
    private static Dictionary<string, object> BuildRequestBody(
        string model,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options,
        bool stream)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, systemPrompt),
        };
        if (options.Temperature != null) requestBody["temperature"] = options.Temperature;
        if (options.ReasoningEffort != null) requestBody["reasoning_effort"] = options.ReasoningEffort;
        if (options.MaxTokens != null) requestBody["max_tokens"] = options.MaxTokens;
        if (options.Tools != null) requestBody["tools"] = options.Tools;
        if (stream)
        {
            requestBody["stream"] = true;
            requestBody["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }
        if (options.ExtraBody != null)
        {
            foreach (var (key, value) in options.ExtraBody)
            {
                requestBody[key] = value;
            }
        }
        return requestBody;
    }

    /// <summary>单个 SSE data 帧的解析结果（供 <see cref="GenerateStream"/> 与单元测试复用）。</summary>
    internal sealed record ParsedChunk(
        string? Text,
        string? Reasoning,
        IReadOnlyList<(int Index, string? Id, string? Name, string? Arguments)>? ToolCallParts,
        Usage? Usage,
        bool Done,
        string? Error);

    /// <summary>解析一个 SSE data 帧为流式增量；解析失败或帧含 error 时通过 Error 字段返回。</summary>
    internal static ParsedChunk ParseChunk(string data)
    {
        if (data == "[DONE]")
        {
            return new ParsedChunk(null, null, null, null, Done: true, null);
        }
        ChatCompletionStreamChunk? chunk;
        try
        {
            chunk = JsonSerializer.Deserialize<ChatCompletionStreamChunk>(data);
        }
        catch (JsonException e)
        {
            return new ParsedChunk(null, null, null, null, Done: false, $"无法解析的流式块: {e.Message}");
        }
        if (chunk?.Error?.Message is { Length: > 0 } errorMessage)
        {
            return new ParsedChunk(null, null, null, null, Done: false, errorMessage);
        }
        if (chunk?.Choices is not { Count: > 0 } choices)
        {
            // 无 choices 的块只可能是 usage 块（include_usage）或空块
            return new ParsedChunk(null, null, null, chunk?.Usage, Done: false, null);
        }
        var delta = choices[0].Delta;
        List<(int, string?, string?, string?)>? parts = null;
        if (delta?.ToolCalls is { Count: > 0 })
        {
            parts = [];
            foreach (var toolCall in delta.ToolCalls)
            {
                parts.Add((toolCall.Index, toolCall.Id, toolCall.Function?.Name, toolCall.Function?.Arguments));
            }
        }
        return new ParsedChunk(delta?.Content, delta?.ReasoningContent, parts, chunk.Usage, Done: false, null);
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

internal class ChatCompletionStreamChunk
{
    [JsonPropertyName("choices")]
    public List<ChatStreamChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }

    [JsonPropertyName("error")]
    public ChatStreamError? Error { get; set; }
}

internal class ChatStreamChoice
{
    [JsonPropertyName("delta")]
    public ChatStreamDelta? Delta { get; set; }
}

internal class ChatStreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<StreamToolCall>? ToolCalls { get; set; }
}

internal class StreamToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public StreamToolCallFunction? Function { get; set; }
}

internal class StreamToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal class ChatStreamError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
#pragma warning restore CS8618
