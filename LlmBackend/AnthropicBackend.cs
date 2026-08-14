using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim _semaphore = new(5, 5);

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string? _defaultModel;
    private readonly int _defaultMaxTokens;

    public AnthropicBackend(string baseUrl, string apiKey, string? defaultModel = null, int defaultMaxTokens = DefaultMaxTokens)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _defaultModel = defaultModel;
        _defaultMaxTokens = defaultMaxTokens > 0 ? defaultMaxTokens : DefaultMaxTokens;
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

        var maxTokens = options.MaxTokens ?? _defaultMaxTokens;
        var thinkingEnabled = !string.IsNullOrWhiteSpace(options.ReasoningEffort);
        var thinkingBudget = 0;
        if (thinkingEnabled)
        {
            thinkingBudget = ThinkingBudgetTokens(options.ReasoningEffort);
            // Anthropic 要求 budget_tokens < max_tokens：不足时抬高 max_tokens
            if (maxTokens <= thinkingBudget)
            {
                maxTokens = thinkingBudget + 4096;
            }
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = BuildMessages(messages),
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            requestBody["system"] = systemPrompt;
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
        if (options.ExtraBody != null)
        {
            foreach (var (key, value) in options.ExtraBody)
            {
                requestBody[key] = value;
            }
        }

        string jsonData = JsonSerializer.Serialize(requestBody);

        await _semaphore.WaitAsync(cancellationToken);
        string responseBody;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages");
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            try
            {
                using var response = await Client.SendAsync(request, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw BackendErrors.Map(responseBody, response.StatusCode, response.Headers.RetryAfter?.Delta);
                }
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NetworkException("Anthropic API 请求超时", e);
            }
            catch (HttpRequestException e)
            {
                throw new NetworkException($"Anthropic API 网络错误: {e.Message}", e);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        var json = JsonSerializer.Deserialize<AnthropicResponse>(responseBody)
            ?? throw new HttpRequestException($"Anthropic API 返回了无法解析的响应: {responseBody}");
        if (json.Content == null)
        {
            throw new HttpRequestException($"Anthropic API 返回空 content: {responseBody}");
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
                    var arguments = block.Input != null
                        ? JsonSerializer.Serialize(block.Input)
                        : "{}";
                    toolCalls.Add(new ToolCall(block.Id ?? "", block.Name ?? "", arguments));
                    break;
            }
        }

        var result = new GenerateResponse(
            textBuilder.Length > 0 ? textBuilder.ToString() : null,
            toolCalls.Count > 0 ? [.. toolCalls] : null,
            reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
            thinkingBlocks.Count > 0 ? JsonSerializer.Serialize(thinkingBlocks) : null);
        var usage = json.Usage ?? new AnthropicUsage();
        return (result, new TokenUsage(usage.TotalTokens, usage.InputTokens, usage.OutputTokens, usage.CachedTokens));
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
            var saved = JsonSerializer.Deserialize<List<ThinkingBlock>>(thinkingBlocksJson);
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
        catch (JsonException)
        {
            // 思考块数据损坏时忽略回放，避免请求被拒
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
            return JsonSerializer.Deserialize<object>(arguments) ?? new Dictionary<string, object>();
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
#pragma warning restore CS8618
