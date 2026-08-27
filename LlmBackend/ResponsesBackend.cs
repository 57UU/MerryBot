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

        string jsonData = JsonSerializer.Serialize(
            BuildRequestBody(messages, systemPrompt, options, model, stream: false), RequestJsonOptions);

        string responseBody;
        // 非流式请求只挂总时长超时：LLM 服务端"算完整轮才发响应头"，首字节约等于
        // 整个生成耗时，TTFB（首 token 延迟）对非流式无意义且会误杀长生成；
        // 超时映射为不可重试的 RequestTimeoutException，避免 LLM 非幂等请求超时重试造成双倍计费
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.TotalGeneration);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, totalCts.Token);
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
                    CommonLib.SimpleLog.Default.Warn($"[LlmBackend] Responses API 未知输出类型: {item.Type}");
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

    /// <summary>构造请求体：非流式与流式共用，流式追加 stream 标志。</summary>
    private static Dictionary<string, object> BuildRequestBody(
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options,
        string model,
        bool stream)
    {
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
        if (stream)
        {
            requestBody["stream"] = true;
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

    /// <summary>
    /// 流式生成（SSE）。output_text.delta → OnTextDelta、reasoning_text/summary 摘要
    /// delta → OnReasoningDelta；function_call 的 id/name 在 output_item.added 到达，
    /// 参数经 function_call_arguments.delta 按 item_id 累积；response.completed 携带
    /// 完整 usage。中途的网络/超时/解析异常归一化为 LlmException。
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
            BuildRequestBody(messages, systemPrompt, options, model, stream: true), RequestJsonOptions);

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.StreamingTotalGeneration);
        using var ttfbCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        ttfbCts.CancelAfter(options.TimeToFirstByte ?? LlmDefaults.TimeToFirstByte);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
            request.Headers.Authorization = new("Bearer", _apiKey);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ttfbCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorBody = await response.Content.ReadAsStringAsync(totalCts.Token);
                throw BackendErrors.Map(errorBody, response.StatusCode, response.Headers.RetryAfter?.Delta);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(totalCts.Token);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            // function_call 条目：item_id → (output_index, call_id, name)；参数分片按 item_id 累积
            var toolCallItems = new Dictionary<string, (int OutputIndex, string CallId, string Name, StringBuilder Arguments)>();
            ResponsesUsage? usage = null;
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
                var streamEvent = JsonSerializer.Deserialize<ResponsesStreamEvent>(data);
                if (streamEvent is null)
                {
                    continue;
                }
                switch (streamEvent.Type)
                {
                    case "response.output_text.delta":
                        if (streamEvent.Delta is { Length: > 0 } text)
                        {
                            textBuilder.Append(text);
                            sink.OnTextDelta(text);
                        }
                        break;
                    case "response.reasoning_text.delta":
                    case "response.reasoning_summary_text.delta":
                        if (streamEvent.Delta is { Length: > 0 } reasoning)
                        {
                            reasoningBuilder.Append(reasoning);
                            sink.OnReasoningDelta(reasoning);
                        }
                        break;
                    case "response.output_item.added":
                        if (streamEvent.Item is { Type: "function_call" } item)
                        {
                            toolCallItems[streamEvent.ItemId ?? string.Empty] = (
                                streamEvent.OutputIndex,
                                item.CallId ?? string.Empty,
                                item.Name ?? string.Empty,
                                new StringBuilder());
                        }
                        break;
                    case "response.function_call_arguments.delta":
                        if (streamEvent.ItemId is { Length: > 0 }
                            && streamEvent.Delta is { Length: > 0 } args
                            && toolCallItems.TryGetValue(streamEvent.ItemId, out var toolCall))
                        {
                            toolCall.Arguments.Append(args);
                            toolCallItems[streamEvent.ItemId] = toolCall;
                        }
                        break;
                    case "response.completed":
                        usage = streamEvent.Response?.Usage ?? usage;
                        break;
                    case "response.failed":
                        throw new InvalidResponseException(
                            $"Responses 流式失败: {streamEvent.Response?.Error?.Message ?? streamEvent.Message ?? "未知错误"}");
                    case "error":
                        throw new InvalidResponseException($"Responses 流式错误: {streamEvent.Message ?? "未知错误"}");
                }
            }

            var toolCalls = toolCallItems.Values
                .OrderBy(item => item.OutputIndex)
                .Select(item => new ToolCall(item.CallId, item.Name, item.Arguments.ToString()))
                .ToArray();
            var result = new GenerateResponse(
                textBuilder.Length > 0 ? textBuilder.ToString() : null,
                toolCalls.Length > 0 ? toolCalls : null,
                reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null);
            var tokenUsage = usage is null
                ? TokenUsage.Zero
                : new TokenUsage(usage.TotalTokens, usage.InputTokens, usage.OutputTokens, usage.CachedTokens);
            sink.OnCompleted(result, tokenUsage);
        }
        catch (LlmException)
        {
            // 含 sink 回调抛出的 LlmException（如重试层检出正文标记）：不得包装，原样穿透
            throw;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"Responses 流式请求超时: {e.Message}");
            throw new RequestTimeoutException("Responses 流式请求超时", e);
        }
        catch (HttpRequestException e)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"Responses 网络错误: {e.Message}");
            throw new NetworkException($"Responses 网络错误: {e.Message}", e);
        }
        catch (IOException e)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"Responses 流读取中断: {e.Message}");
            throw new NetworkException($"Responses 流读取中断: {e.Message}", e);
        }
        catch (JsonException e)
        {
            CommonLib.SimpleLog.Default.Warn(e, $"Responses 流式响应解析失败: {e.Message}");
            throw new InvalidResponseException($"Responses 流式响应解析失败: {e.Message}", e);
        }
    }

    /// <summary>
    /// 构造 Responses API 的 input 数组。
    ///
    /// Responses API 的 function_call 与 function_call_output 都是 input 数组
    /// 中的顶层条目，不能使用 Chat Completions 的 assistant.tool_calls 结构；
    /// 否则 provider 找不到对应的 function_call，就会拒绝后续 output。
    /// </summary>
    internal static List<object> BuildInput(IList<Message> messages)
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
                    var assistantContent = message.content?.ToList() ?? [];
                    if (assistantContent.Count > 0)
                    {
                        input.Add(new Dictionary<string, object>
                        {
                            ["role"] = "assistant",
                            // assistant 是模型历史输出，Responses provider 要求其文本块
                            // 使用 output_text；input_text 仅适用于 user 输入消息。
                            ["content"] = BuildContent(assistantContent, imageType: "input_image", textType: "output_text"),
                        });
                    }

                    foreach (var call in message.toolCalls ?? [])
                    {
                        input.Add(new Dictionary<string, object>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["arguments"] = NormalizeFunctionArguments(call.Arguments),
                        });
                    }
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

    /// <summary>
    /// Responses API 要求 function_call.arguments 是合法 JSON 字符串。
    /// 个别模型可能返回空参数或截断/损坏的参数；工具执行层会将空参数按
    /// {} 处理，但回放历史时仍必须传合法 JSON，否则下一轮请求会被 provider
    /// 以 "arguments must be valid JSON" 拒绝。
    /// </summary>
    private static string NormalizeFunctionArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return "{}";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            return arguments;
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
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

internal class ResponsesStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; set; }

    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("item")]
    public ResponsesStreamItem? Item { get; set; }

    [JsonPropertyName("response")]
    public ResponsesStreamResponse? Response { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal class ResponsesStreamItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal class ResponsesStreamResponse
{
    [JsonPropertyName("usage")]
    public ResponsesUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public ResponsesStreamError? Error { get; set; }
}

internal class ResponsesStreamError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
#pragma warning restore CS8618
