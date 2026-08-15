namespace LlmBackend;

/// <summary>
/// LLM 请求失败的标准异常基类。调用方可通过 Retryable 属性判断是否需要重试。
/// </summary>
public abstract class LlmException : Exception
{
    /// <summary>HTTP 状态码；网络错误等非 HTTP 场景为 null</summary>
    public int? StatusCode { get; }

    /// <summary>是否可安全重试（限速、服务器错误、网络错误为 true；参数错误、鉴权失败为 false）</summary>
    public bool Retryable { get; }

    protected LlmException(string message, int? statusCode, bool retryable, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }
}

/// <summary>限速（HTTP 429），可重试</summary>
public sealed class RateLimitException : LlmException
{
    /// <summary>服务端通过 Retry-After 头建议的等待时间，未提供时为 null</summary>
    public TimeSpan? RetryAfter { get; }

    public RateLimitException(string message, int statusCode, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, statusCode, retryable: true, innerException)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>鉴权失败（HTTP 401/403），检查 API Key，不可重试</summary>
public sealed class AuthenticationException : LlmException
{
    public AuthenticationException(string message, int statusCode, Exception? innerException = null)
        : base(message, statusCode, retryable: false, innerException)
    {
    }
}

/// <summary>模型不存在（HTTP 404），不可重试</summary>
public sealed class ModelNotFoundException : LlmException
{
    public ModelNotFoundException(string message, int statusCode, Exception? innerException = null)
        : base(message, statusCode, retryable: false, innerException)
    {
    }
}

/// <summary>请求无效（HTTP 400 等其余 4xx），不可重试</summary>
public class InvalidRequestException : LlmException
{
    public InvalidRequestException(string message, int statusCode, Exception? innerException = null)
        : base(message, statusCode, retryable: false, innerException)
    {
    }
}

/// <summary>上下文超长（400 且响应含 context length 特征），需要压缩或裁剪历史后重试</summary>
public sealed class ContextLengthExceededException : InvalidRequestException
{
    public ContextLengthExceededException(string message, int statusCode, Exception? innerException = null)
        : base(message, statusCode, innerException)
    {
    }
}

/// <summary>服务器错误（HTTP 5xx / 408），可重试</summary>
public sealed class ServerErrorException : LlmException
{
    public ServerErrorException(string message, int statusCode, Exception? innerException = null)
        : base(message, statusCode, retryable: true, innerException)
    {
    }
}

/// <summary>网络错误（连接失败等，无 HTTP 状态码），可重试</summary>
public sealed class NetworkException : LlmException
{
    public NetworkException(string message, Exception? innerException = null)
        : base(message, statusCode: null, retryable: true, innerException)
    {
    }
}

/// <summary>请求超时（无 HTTP 状态码）。不可重试：LLM 请求非幂等，
/// 服务端可能已开始计费，重试存在双倍计费风险；由调用方决定是否降级。</summary>
public sealed class RequestTimeoutException : LlmException
{
    public RequestTimeoutException(string message, Exception? innerException = null)
        : base(message, statusCode: null, retryable: false, innerException)
    {
    }
}

/// <summary>服务端返回了无法解析/结构异常的响应（HTTP 200 但 JSON 不符合预期），不可重试。</summary>
public sealed class InvalidResponseException : LlmException
{
    public InvalidResponseException(string message, Exception? innerException = null)
        : base(message, statusCode: null, retryable: false, innerException)
    {
    }
}
