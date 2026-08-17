using System.Net;
using System.Text.Json;

namespace LlmBackend;

/// <summary>
/// 各 Backend 共用的 HTTP 错误映射：按状态码与响应体归类为可重试/不可重试的 LlmException。
/// OpenAI 与 Anthropic 的错误响应均为 { "error": { "message": "..." } } 形状，可直接复用。
/// </summary>
public static class BackendErrors
{
    private static readonly string[] ContextLengthKeywords =
    [
        "context length", "context_length", "maximum context", "max context",
        "too many tokens", "prompt is too long",
    ];

    public static bool IsContextLengthError(string responseBody)
        => ContextLengthKeywords.Any(keyword => responseBody.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从响应体中提取可读的错误信息：优先 error.message 字段，非 JSON 时退回原文；
    /// 统一截断到 <paramref name="maxLength"/> 字符，避免大响应体撑爆异常消息。
    /// </summary>
    public static string Shorten(string responseBody, int maxLength = 200)
    {
        var message = responseBody ?? string.Empty;
        try
        {
            var error = JsonSerializer.Deserialize(message, LlmBackendJsonContext.Default.ApiErrorEnvelope);
            if (!string.IsNullOrEmpty(error?.Error?.Message))
            {
                message = error.Error.Message;
            }
        }
        catch (JsonException)
        {
            // 响应体不是 JSON，直接使用原文
        }
        return message.Length <= maxLength ? message : message[..maxLength] + "…";
    }

    public static LlmException Map(string responseBody, HttpStatusCode statusCode, TimeSpan? retryAfter)
    {
        string message = $"API 错误 ({(int)statusCode} {statusCode})";
        try
        {
            var error = JsonSerializer.Deserialize(responseBody, LlmBackendJsonContext.Default.ApiErrorEnvelope);
            if (!string.IsNullOrEmpty(error?.Error?.Message))
            {
                message += $": {error.Error.Message}";
            }
        }
        catch (JsonException)
        {
            // 响应体不是 JSON，直接拼接原文
        }
        if (message.Length < responseBody.Length && responseBody.Length <= 500)
        {
            message += $": {responseBody}";
        }

        return statusCode switch
        {
            >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout => new ServerErrorException(message, (int)statusCode),
            HttpStatusCode.TooManyRequests => new RateLimitException(message, (int)statusCode, retryAfter),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AuthenticationException(message, (int)statusCode),
            HttpStatusCode.NotFound => new ModelNotFoundException(message, (int)statusCode),
            _ => IsContextLengthError(responseBody)
                ? new ContextLengthExceededException(message, (int)statusCode)
                : new InvalidRequestException(message, (int)statusCode),
        };
    }

    internal sealed class ApiErrorEnvelope
    {
        public ApiErrorBody? Error { get; set; }
    }

    internal sealed class ApiErrorBody
    {
        public string? Message { get; set; }
    }
}
