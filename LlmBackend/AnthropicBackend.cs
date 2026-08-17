using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LlmBackend;

/// <summary>
/// Anthropic Messages API (/v1/messages) 后端。与 OpenAI 系的主要差异：
/// 认证走 x-api-key + anthropic-version 头；system prompt 为顶层字段；
/// 工具调用为 content 里的 tool_use 块、工具结果为 user 消息里的 tool_result 块；
/// max_tokens 为必填项。
/// </summary>
public class AnthropicBackend : Backend
{
    private const string ApiVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    private static readonly Dictionary<string, object> CacheControl = new() { ["type"] = "ephemeral" };

    // 超时全部由 LlmOptions 的两段 CTS 控制（首字节 + 总时长），HttpClient 本身不设超时
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string? _defaultModel;
    private readonly int _defaultMaxTokens;
    private readonly bool _enablePromptCache;

    public AnthropicBackend(string baseUrl, string apiKey, string? defaultModel = null, int defaultMaxTokens = DefaultMaxTokens, bool enablePromptCache = false)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _defaultModel = defaultModel;
        _defaultMaxTokens = defaultMaxTokens > 0 ? defaultMaxTokens : DefaultMaxTokens;
        _enablePromptCache = enablePromptCache;
    }

    /// <summary>把统一的 ReasoningEffort 档位映射为 Anthropic thinking 预算。</summary>
    private static int ThinkingBudgetTokens(string effort) => effort.ToLowerInvariant() switch
    {
        "medium" => 16_384,
        "high" => 32_768,
        _ => 4_096, // low 及未知档位
    };

    public async Task<(GenerateResponse, TokenUsage)> Generate(
        CancellationToken cancellationToken,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options)
    {
        string model = options.Model ?? _defaultModel
            ?? throw new ArgumentException("模型未指定：请在 LlmOptions.Model 或构造函数 defaultModel 中提供", nameof(options));

        var (maxTokens, thinkingEnabled, thinkingBudget) = ResolveGenerationConfig(options);

        var apiMessages = BuildMessages(messages);
        if (_enablePromptCache)
        {
            ApplyCacheBreakpoints(apiMessages);
        }
        string jsonData = (JsonNodeConverter.ToNode(
            BuildRequestBody(systemPrompt, options, model, maxTokens, thinkingEnabled, thinkingBudget, apiMessages, stream: false)) ?? new JsonObject()).ToJsonString();

        string responseBody;
        // 非流式请求只挂总时长超时：LLM 服务端"算完整轮才发响应头"，首字节约等于
        // 整个生成耗时，TTFB（首 token 延迟）对非流式无意义且会误杀长生成；
        // 超时映射为不可重试的 RequestTimeoutException，避免 LLM 非幂等请求超时重试造成双倍计费
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.TotalGeneration);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages");
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
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
            throw new RequestTimeoutException("Anthropic API 请求超时", e);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkException($"Anthropic API 网络错误: {e.Message}", e);
        }

        var json = JsonSerializer.Deserialize(responseBody, LlmBackendJsonContext.Default.AnthropicResponse)
            ?? throw new InvalidResponseException($"Anthropic API 返回了无法解析的响应: {BackendErrors.Shorten(responseBody)}");
        if (json.Content == null)
        {
            throw new InvalidResponseException($"Anthropic API 返回空 content: {BackendErrors.Shorten(responseBody)}");
        }

        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var thinkingBlocks = new List<ThinkingBlock>();
        foreach (var block in json.Content)
        {
            switch (block.Type)
            {
                case "text" when !string.IsNullOrEmpty(block.Text):
                    textBuilder.Append(block.Text);
                    break;
                case "thinking":
                    // 深度思考块带加密签名，必须持久化供后续轮次原样回传
                    thinkingBlocks.Add(new ThinkingBlock
                    {
                        Type = "thinking",
                        Thinking = block.Thinking ?? string.Empty,
                        Signature = block.Signature ?? string.Empty,
                    });
                    if (!string.IsNullOrEmpty(block.Thinking))
                    {
                        reasoningBuilder.Append(block.Thinking);
                    }
                    break;
                case "redacted_thinking":
                    // 触及安全过滤的思考内容被脱敏，回传时原样携带 data
                    thinkingBlocks.Add(new ThinkingBlock
                    {
                        Type = "redacted_thinking",
                        Data = block.Data ?? string.Empty,
                    });
                    break;
                case "tool_use":
                    var arguments = block.Input is { ValueKind: not JsonValueKind.Undefined } input
                        ? input.GetRawText()
                        : "{}";
                    toolCalls.Add(new ToolCall(block.Id ?? "", block.Name ?? "", arguments));
                    break;
            }
        }

        var result = new GenerateResponse(
            textBuilder.Length > 0 ? textBuilder.ToString() : null,
            toolCalls.Count > 0 ? [.. toolCalls] : null,
            reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
            thinkingBlocks.Count > 0 ? JsonSerializer.Serialize(thinkingBlocks, LlmBackendJsonContext.Default.ListThinkingBlock) : null);
        var usage = json.Usage ?? new AnthropicUsage();
        return (result, new TokenUsage(usage.TotalTokens, usage.InputTokens, usage.OutputTokens, usage.CachedTokens));
    }

    /// <summary>
    /// 解析生成配置：max_tokens（含 thinking 预算抬升）与 thinking 开关。
    /// thinkingEnabled 由 IsNullOrWhiteSpace 保证非空；Anthropic 要求
    /// budget_tokens &lt; max_tokens，且 thinking 计入 max_tokens 消耗：预算占满时
    /// 模型将没有输出余量，max_tokens 必须抬到预算之上。保留用户配置意图——
    /// 仅在用户配置值不足时抬升，取"用户配置值"与"预算 + 1 最小余量"的较大者，
    /// 而不是无条件放大到 预算+4096（避免 high 档把用户配置的 4096 覆盖成 36864）；
    /// 用户显式配置更大的 MaxOutputTokens 时保持不变。
    /// </summary>
    private (int maxTokens, bool thinkingEnabled, int thinkingBudget) ResolveGenerationConfig(LlmOptions options)
    {
        var maxTokens = options.MaxTokens ?? _defaultMaxTokens;
        var thinkingEnabled = !string.IsNullOrWhiteSpace(options.ReasoningEffort);
        var thinkingBudget = 0;
        if (thinkingEnabled)
        {
            thinkingBudget = ThinkingBudgetTokens(options.ReasoningEffort!);
            if (maxTokens <= thinkingBudget)
            {
                maxTokens = Math.Max(maxTokens, thinkingBudget + 1);
            }
        }
        return (maxTokens, thinkingEnabled, thinkingBudget);
    }

    /// <summary>构造请求体：非流式与流式共用，流式追加 stream 标志。</summary>
    private Dictionary<string, object> BuildRequestBody(
        string systemPrompt,
        LlmOptions options,
        string model,
        int maxTokens,
        bool thinkingEnabled,
        int thinkingBudget,
        List<object> apiMessages,
        bool stream)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = apiMessages,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            // 启用缓存时 system 必须为块数组才能在文本块上打 cache_control 断点；
            // 关闭时保持字符串下发，请求体与未启用时完全一致
            requestBody["system"] = _enablePromptCache
                ? new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt,
                        ["cache_control"] = CacheControl,
                    },
                }
                : systemPrompt;
        }
        if (thinkingEnabled)
        {
            // 开启 thinking 时 API 不允许 temperature/top_p/top_k，直接不下发
            requestBody["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = thinkingBudget,
            };
        }
        else if (options.Temperature != null)
        {
            requestBody["temperature"] = options.Temperature;
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
    /// 流式生成（SSE，event: 行 + data: 行）。text_delta → OnTextDelta、
    /// thinking_delta → OnReasoningDelta；thinking 块在 content_block_stop 收尾时
    /// 组装完整块（完整 signature 只在最后一个 thinking_delta 携带）；tool_use 的
    /// id/name 在 content_block_start 到达，参数经 input_json_delta 累积。
    /// 中途的网络/超时/解析异常归一化为 LlmException。
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

        var (maxTokens, thinkingEnabled, thinkingBudget) = ResolveGenerationConfig(options);
        var apiMessages = BuildMessages(messages);
        if (_enablePromptCache)
        {
            ApplyCacheBreakpoints(apiMessages);
        }
        string jsonData = (JsonNodeConverter.ToNode(
            BuildRequestBody(systemPrompt, options, model, maxTokens, thinkingEnabled, thinkingBudget, apiMessages, stream: true)) ?? new JsonObject()).ToJsonString();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout ?? LlmDefaults.StreamingTotalGeneration);
        using var ttfbCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        ttfbCts.CancelAfter(options.TimeToFirstByte ?? LlmDefaults.TimeToFirstByte);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages");
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
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
            // thinking 块累积（含签名，须原样回传）；redacted_thinking 在 start 事件即完整
            var thinkingBlocks = new List<ThinkingBlock>();
            var pendingThinking = new Dictionary<int, PendingThinking>();
            var pendingToolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            AnthropicUsage? usage = null;
            string? line;
            while ((line = await reader.ReadLineAsync(totalCts.Token)) != null)
            {
                if (line.Length == 0)
                {
                    continue; // SSE 帧分隔空行
                }
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    continue; // 事件名已由 data 内的 type 字段表达，无需单独跟踪
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }
                var streamEvent = JsonSerializer.Deserialize(data, LlmBackendJsonContext.Default.AnthropicStreamEvent);
                if (streamEvent is null)
                {
                    continue;
                }
                switch (streamEvent.Type)
                {
                    case "message_start":
                        if (streamEvent.Message?.Usage != null)
                        {
                            usage = streamEvent.Message.Usage;
                        }
                        break;
                    case "content_block_start":
                        if (streamEvent.ContentBlock != null)
                        {
                            switch (streamEvent.ContentBlock.Type)
                            {
                                case "thinking":
                                    pendingThinking[streamEvent.Index] = new PendingThinking(streamEvent.ContentBlock.Thinking ?? string.Empty);
                                    break;
                                case "redacted_thinking":
                                    thinkingBlocks.Add(new ThinkingBlock
                                    {
                                        Type = "redacted_thinking",
                                        Data = streamEvent.ContentBlock.Data ?? string.Empty,
                                    });
                                    break;
                                case "tool_use":
                                    pendingToolCalls[streamEvent.Index] = (
                                        streamEvent.ContentBlock.Id ?? string.Empty,
                                        streamEvent.ContentBlock.Name ?? string.Empty,
                                        new StringBuilder());
                                    break;
                            }
                        }
                        break;
                    case "content_block_delta":
                        if (streamEvent.Delta != null)
                        {
                            switch (streamEvent.Delta.Type)
                            {
                                case "text_delta":
                                    if (streamEvent.Delta.Text is { Length: > 0 } text)
                                    {
                                        textBuilder.Append(text);
                                        sink.OnTextDelta(text);
                                    }
                                    break;
                                case "thinking_delta":
                                    if (streamEvent.Delta.Thinking is { Length: > 0 } thinking)
                                    {
                                        reasoningBuilder.Append(thinking);
                                        sink.OnReasoningDelta(thinking);
                                        if (pendingThinking.TryGetValue(streamEvent.Index, out var pending))
                                        {
                                            pending.Text.Append(thinking);
                                        }
                                    }
                                    // 完整 signature 只在最后一个 thinking_delta 携带
                                    if (streamEvent.Delta.Signature is { Length: > 0 }
                                        && pendingThinking.TryGetValue(streamEvent.Index, out var sigPending))
                                    {
                                        sigPending.Signature = streamEvent.Delta.Signature;
                                    }
                                    break;
                                case "input_json_delta":
                                    if (streamEvent.Delta.PartialJson is { Length: > 0 }
                                        && pendingToolCalls.TryGetValue(streamEvent.Index, out var toolCall))
                                    {
                                        toolCall.Arguments.Append(streamEvent.Delta.PartialJson);
                                    }
                                    break;
                            }
                        }
                        break;
                    case "content_block_stop":
                        // thinking 块收尾：累积文字 + 最终 signature
                        if (pendingThinking.Remove(streamEvent.Index, out var finishedThinking))
                        {
                            thinkingBlocks.Add(new ThinkingBlock
                            {
                                Type = "thinking",
                                Thinking = finishedThinking.Text.ToString(),
                                Signature = finishedThinking.Signature,
                            });
                        }
                        break;
                    case "message_delta":
                        // message_start 带 input/cache 用量，message_delta 带 output 用量
                        if (streamEvent.Usage != null)
                        {
                            usage ??= new AnthropicUsage();
                            usage.OutputTokens = streamEvent.Usage.OutputTokens;
                        }
                        break;
                    case "message_stop":
                        break; // 流自然结束，跳出循环后统一组装终结事件
                    case "error":
                        throw MapStreamError(streamEvent.Error);
                }
            }

            var toolCalls = pendingToolCalls
                .OrderBy(pair => pair.Key)
                .Select(pair => new ToolCall(pair.Value.Id, pair.Value.Name, pair.Value.Arguments.ToString()))
                .ToArray();
            var result = new GenerateResponse(
                textBuilder.Length > 0 ? textBuilder.ToString() : null,
                toolCalls.Length > 0 ? toolCalls : null,
                reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                thinkingBlocks.Count > 0 ? JsonSerializer.Serialize(thinkingBlocks, LlmBackendJsonContext.Default.ListThinkingBlock) : null);
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
            throw new RequestTimeoutException("Anthropic 流式请求超时", e);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkException($"Anthropic 网络错误: {e.Message}", e);
        }
        catch (IOException e)
        {
            throw new NetworkException($"Anthropic 流读取中断: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new InvalidResponseException($"Anthropic 流式响应解析失败: {e.Message}", e);
        }
    }

    /// <summary>流中 error 事件映射为 LlmException：overloaded/rate_limit 可重试，其余不可重试。</summary>
    private static LlmException MapStreamError(AnthropicStreamError? error)
    {
        var message = error?.Message is { Length: > 0 } ? error.Message : "未知流式错误";
        return error?.Type switch
        {
            "overloaded_error" => new ServerErrorException(message, 503),
            "rate_limit_error" => new RateLimitException(message, 429),
            _ => new InvalidResponseException($"Anthropic 流式错误: {message}"),
        };
    }

    /// <summary>流式 thinking 块累积器（值类型元组无法就地改 Signature，用可变类）。</summary>
    private sealed class PendingThinking
    {
        public readonly StringBuilder Text;
        public string Signature = string.Empty;

        public PendingThinking(string initial) => Text = new StringBuilder(initial);
    }

    /// <summary>
    /// 构造 messages：assistant 的工具调用为 content 中的 tool_use 块；
    /// 连续的 tool 结果消息合并为一条 user 消息中的多个 tool_result 块（Anthropic 要求）。
    /// </summary>
    private static List<object> BuildMessages(IList<Message> messages)
    {
        var result = new List<object>(messages.Count);
        List<object>? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is { Count: > 0 })
            {
                result.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = pendingToolResults,
                });
            }
            pendingToolResults = null;
        }

        foreach (var message in messages)
        {
            if (message.role.Value == "tool")
            {
                pendingToolResults ??= [];
                pendingToolResults.Add(new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = message.toolCallId,
                    ["content"] = ExtractText(message.content),
                });
                continue;
            }

            FlushToolResults();

            var blocks = new List<object>();

            // Anthropic 深度思考回放：assistant 消息的 thinking 块（含签名）必须原样
            // 回传且位于文本/tool_use 之前，否则 API 拒绝请求
            if (message.role.Value == "assistant" && !string.IsNullOrEmpty(message.thinkingBlocks))
            {
                ReplayThinkingBlocks(blocks, message.thinkingBlocks);
            }

            foreach (var part in message.content ?? [])
            {
                switch (part)
                {
                    case MessagePartText t:
                        blocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = t.text ?? string.Empty,
                        });
                        break;
                    case MessagePartImage img:
                        blocks.Add(BuildImageBlock(img.image));
                        break;
                }
            }

            if (message.role.Value == "assistant" && message.toolCalls?.Any() == true)
            {
                foreach (var call in message.toolCalls)
                {
                    blocks.Add(new Dictionary<string, object>
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ParseArguments(call.Arguments),
                    });
                }
            }

            if (blocks.Count == 0)
            {
                continue; // 空消息（如无内容且无工具调用的 assistant 占位）直接跳过
            }
            result.Add(new Dictionary<string, object>
            {
                ["role"] = message.role.Value == "assistant" ? "assistant" : "user",
                ["content"] = blocks,
            });
        }

        FlushToolResults();
        return result;
    }

    /// <summary>把持久化的思考块 JSON 原样重建为 content 块；解析失败时静默跳过。</summary>
    private static void ReplayThinkingBlocks(List<object> blocks, string thinkingBlocksJson)
    {
        try
        {
            var saved = JsonSerializer.Deserialize(thinkingBlocksJson, LlmBackendJsonContext.Default.ListThinkingBlock);
            if (saved == null) return;
            foreach (var tb in saved)
            {
                switch (tb.Type)
                {
                    case "thinking":
                        blocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = tb.Thinking ?? string.Empty,
                            ["signature"] = tb.Signature ?? string.Empty,
                        });
                        break;
                    case "redacted_thinking":
                        blocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = tb.Data ?? string.Empty,
                        });
                        break;
                }
            }
        }
        catch (JsonException e)
        {
            // 思考块数据损坏时静默跳过会掩盖根因（thinking 块缺失/错位会被 API 拒绝，
            // 且多轮中无法修复），抛出明确异常让调用方可见
            throw new InvalidResponseException(
                $"Anthropic 思考块回放数据损坏，无法重建 thinking 块: {BackendErrors.Shorten(thinkingBlocksJson)}", e);
        }
    }

    /// <summary>
    /// 给最后一条消息的最后一个可缓存内容块（文本/图片/tool_result）打 cache_control
    /// 断点：覆盖 system + 全部历史。tool calling 多轮时断点随轮次前移到最后的
    /// 工具结果，每轮只对新增的工具往返内容付一次缓存写入（5m TTL 写入 1.25x、
    /// 读取 0.1x）。thinking 块不支持缓存，天然跳过。
    /// </summary>
    private static void ApplyCacheBreakpoints(List<object> messages)
    {
        if (messages.Count == 0) return;
        if (messages[^1] is not Dictionary<string, object> last
            || !last.TryGetValue("content", out var contentObj)
            || contentObj is not List<object> content)
        {
            return;
        }
        for (int i = content.Count - 1; i >= 0; i--)
        {
            if (content[i] is not Dictionary<string, object> block) continue;
            var type = block.TryGetValue("type", out var t) ? t as string : null;
            if (type is "text" or "image" or "tool_result")
            {
                block["cache_control"] = CacheControl;
                return;
            }
        }
    }

    /// <summary>Anthropic 不支持 URL 图片，data URL 拆解为 base64 块；非 data URL 直接报错跳过。</summary>
    private static Dictionary<string, object> BuildImageBlock(string image)
    {
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = image.IndexOf(',');
            if (comma > 5)
            {
                var header = image[5..comma]; // 形如 image/png;base64
                var mediaType = header.Split(';')[0];
                var data = image[(comma + 1)..];
                return new Dictionary<string, object>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object>
                    {
                        ["type"] = "base64",
                        ["media_type"] = string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType,
                        ["data"] = data,
                    },
                };
            }
        }
        return new Dictionary<string, object>
        {
            ["type"] = "text",
            ["text"] = $"[无法识别的图片数据: {image[..Math.Min(image.Length, 80)]}]",
        };
    }

    /// <summary>工具参数是 JSON 字符串，解析为对象；解析失败退回空对象避免请求被拒。</summary>
    private static object ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object>();
        }
        try
        {
            // AOT 兼容:任意 JSON 用 JsonDocument 解析,不依赖反射反序列化
            using var doc = JsonDocument.Parse(arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    private static List<object> BuildTools(IEnumerable<ToolDef> tools)
        => tools.Select(tool => (object)new Dictionary<string, object>
        {
            ["name"] = tool.function.name,
            ["description"] = tool.function.description,
            ["input_schema"] = (object?)tool.function.parameters ?? new Dictionary<string, object>(),
        }).ToList();

    private static string ExtractText(IEnumerable<MessagePart>? parts)
        => string.Concat((parts ?? []).OfType<MessagePartText>().Select(t => t.text ?? string.Empty));
}

#pragma warning disable CS8618 // 响应 DTO，非空字段由 JSON 反序列化填充
internal class AnthropicResponse
{
    [JsonPropertyName("content")]
    public List<AnthropicContentBlock>? Content { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

/// <summary>
/// Anthropic 思考块的最小持久化表示：thinking 块携带加密签名（signature），
/// 触及安全过滤的内容以 redacted_thinking 的 data 返回，多轮 tool calling
/// 必须原样回传。序列化到 GenerateResponse.ThinkingBlocks / Message.thinkingBlocks。
/// </summary>
internal class ThinkingBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "thinking" | "redacted_thinking"

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

internal class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }

    [JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;

    [JsonIgnore]
    public int CachedTokens => CacheReadInputTokens + CacheCreationInputTokens;
}

internal class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public AnthropicStreamMessage? Message { get; set; }

    [JsonPropertyName("content_block")]
    public AnthropicContentBlock? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicStreamDelta? Delta { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public AnthropicStreamError? Error { get; set; }
}

internal class AnthropicStreamMessage
{
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal class AnthropicStreamDelta
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }
}

internal class AnthropicStreamError
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
#pragma warning restore CS8618
