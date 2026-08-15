using LlmBackend;

namespace LlmClient;

/// <summary>
/// LLM 客户端，封装 Backend 调用并实现重试：优先使用异常携带的避让时间
/// （RateLimitException.RetryAfter），否则按 initialDelay 指数退避
/// </summary>
public class Client
{
    private readonly Backend backend;
    private readonly ClientConfig clientConfig;

    public Client(Backend backend, ClientConfig clientConfig)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.clientConfig = clientConfig;
    }

    /// <summary>
    /// 调用后端生成回复，最多尝试 maxAttempt 次。
    /// 仅重试 Retryable 异常（限速/服务器错误/网络错误）；不可重试异常与取消直接抛出。
    /// 请求超时（RequestTimeoutException）不可重试：LLM 请求非幂等，超时重试可能双倍计费。
    /// </summary>
    public async Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await backend.Generate(cancellationToken, messages, systemPrompt, options);
            }
            catch (LlmException e) when (e.Retryable && attempt < clientConfig.maxAttempt)
            {
                TimeSpan delay = GetDelay(e, attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 计算重试等待时间：优先使用服务端避让时间（Retry-After），否则指数退避；
    /// 两者都设 30 秒上限，避免异常大的 Retry-After 长时间空等
    /// </summary>
    private TimeSpan GetDelay(LlmException e, int attempt)
    {
        var max = TimeSpan.FromSeconds(30);
        if (e is RateLimitException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
        {
            return retryAfter > max ? max : retryAfter;
        }
        // 1L 移位避免 int 溢出（attempt 超过 31 时 1<<n 为负/零）；指数整体仍受 30 秒上限约束
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var backoff = clientConfig.initialDelay * multiplier;
        return backoff > max ? max : backoff;
    }
}

public record ClientConfig(
    int maxAttempt,
    TimeSpan initialDelay
    );
