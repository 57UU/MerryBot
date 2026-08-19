using CommonLib;
using LlmBackend;

namespace LlmClient;

/// <summary>流式重建（reset）的原因分类（失败细节见 cause 异常）。</summary>
public enum StreamResetReason
{
    /// <summary>NetworkException：连接失败/中途断流</summary>
    NetworkError,
    /// <summary>ServerErrorException：5xx/408/overloaded</summary>
    ServerError,
    /// <summary>RateLimitException：429 限速</summary>
    RateLimited,
    /// <summary>正文检出工具调用标记（DSML/XML 标签或 JSON 结构）</summary>
    StrayToolCallMarkup,
    /// <summary>其余可重试异常</summary>
    Other,
}

/// <summary>
/// 重试层的流式接收端：在 <see cref="IStreamSink"/> 之上增加 <see cref="OnReset"/>。
/// Client 检出可重试失败并决定重建流时回调 OnReset——此前推送的全部增量作废，
/// 随后推送新一次的增量；终态失败（不可重试/预算耗尽）不回调 OnReset，
/// 由 GenerateStream 直接抛异常。
/// Client 不定义"段"（segment）语义：段的划分由消费者解释——调用开始或
/// OnReset 之后、到下一个 OnReset/OnCompleted 之前的增量属于同一段。
/// </summary>
public interface IResettableStreamSink : IStreamSink
{
    /// <summary>此前推送的全部增量作废，流将重建重试；仅在确定会重试时回调。</summary>
    void OnReset(StreamResetReason reason, Exception cause);
}

/// <summary>
/// LLM 客户端，封装 Backend 调用并实现重试：优先使用异常携带的避让时间
/// （RateLimitException.RetryAfter），否则按 initialDelay 指数退避。
/// 后端引用可运行时替换（<see cref="UpdateBackend"/>），切换在请求间隙生效——
/// 每次 Generate/GenerateStream 开始时读取当前引用快照，同一请求全程用同一后端。
///
/// 流式重试基于 reset 语义：任何可重试失败（含中途断流、正文检出工具调用标记）
/// 在预算内都会回调 <see cref="IResettableStreamSink.OnReset"/> 后重建流，
/// 消费者据此丢弃已收到的部分增量；不再受"首元素产出前才可重试"限制。
/// </summary>
public class Client
{
    private readonly object _sync = new();
    private Backend backend;
    private readonly ClientConfig clientConfig;
    private readonly ISimpleLogger _logger;

    public Client(Backend backend, ClientConfig clientConfig, ISimpleLogger? logger = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.clientConfig = clientConfig;
        _logger = logger ?? SimpleLog.Default;
    }

    /// <summary>运行时替换后端（下一次 Generate/GenerateStream 即生效）。</summary>
    public void UpdateBackend(Backend newBackend)
    {
        ArgumentNullException.ThrowIfNull(newBackend);
        lock (_sync)
        {
            backend = newBackend;
        }
    }

    /// <summary>当前后端引用快照（线程安全；不持锁 await，切换时并发请求看到一致后端）。</summary>
    private Backend CurrentBackend
    {
        get
        {
            lock (_sync)
            {
                return backend;
            }
        }
    }

    /// <summary>
    /// 调用后端生成回复，最多尝试 maxAttempt 次。
    /// 仅重试 Retryable 异常（限速/服务器错误/网络错误）；不可重试异常与取消直接抛出。
    /// 请求超时（RequestTimeoutException）不可重试：LLM 请求非幂等，超时重试可能双倍计费。
    /// 携带工具的请求中，若模型把工具调用以文本标记输出到正文
    /// （StrayToolCallMarkup），终检命中后额外重试一次（不消耗 maxAttempt 预算）。
    /// </summary>
    public async Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
    {
        bool strayRetried = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var result = await CurrentBackend.Generate(cancellationToken, messages, systemPrompt, options);
                // 非流式终检：正文整体到达后才可能判断，命中则重试一次（尚未返回调用方，透明）；
                // 重试后仍命中则抛出（兜底：不把标记文本当正常回复返回）
                if (HasTools(options)
                    && StrayToolCallDetector.IsStrayToolCallMarkup(result.Item1.Content))
                {
                    var ex = StrayMarkupException(result.Item1.Content);
                    if (strayRetried)
                    {
                        throw ex;
                    }
                    strayRetried = true;
                    _logger.Warn($"模型把工具调用输出到正文，额外重试一次（第 {attempt} 次尝试）");
                    await Task.Delay(GetDelay(ex, 1), cancellationToken);
                    continue;
                }
                return result;
            }
            catch (LlmException e) when (e.Retryable && e is not StrayToolCallMarkupException && attempt < clientConfig.maxAttempt)
            {
                TimeSpan delay = GetDelay(e, attempt);
                var retryAfter = e is RateLimitException { RetryAfter: { } ra } ? $"，Retry-After={ra.TotalSeconds:F0} 秒" : "";
                _logger.Warn(e, $"LLM 请求第 {attempt}/{clientConfig.maxAttempt} 次尝试失败，{delay.TotalSeconds:F1} 秒后重试{retryAfter}: {e.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 流式调用后端生成回复，可重试失败最多尝试 maxAttempt 次。
    /// 每次尝试经 MarkupGuardSink 包装（携带工具的请求）：增量即时透传不做扣留，
    /// 完成时对全量正文做工具调用标记检测（开头/结尾窗口），命中则抛
    /// StrayToolCallMarkupException 走统一 reset 重试——本段已推送的增量由消费者
    /// 按 <see cref="IResettableStreamSink.OnReset"/> 语义丢弃。
    /// 可重试失败且预算未尽时回调 OnReset 后重建流（中途断流同理）；
    /// 不可重试异常、预算耗尽与用户取消直接抛出（不发 reset）。
    /// </summary>
    public async Task GenerateStream(
        IResettableStreamSink sink,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            IStreamSink attemptSink = HasTools(options) ? new MarkupGuardSink(sink) : sink;
            try
            {
                await CurrentBackend.GenerateStream(attemptSink, messages, systemPrompt, options, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 用户取消：不发 reset，直接传播
            }
            catch (LlmException e) when (e.Retryable && attempt < clientConfig.maxAttempt)
            {
                var reason = MapReason(e);
                var delay = GetDelay(e, attempt);
                _logger.Warn(e, $"LLM 流式第 {attempt}/{clientConfig.maxAttempt} 次尝试失败（{reason}），{delay.TotalSeconds:F1} 秒后重建流: {e.Message}");
                sink.OnReset(reason, e);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>请求是否携带工具（仅此时才可能发生工具调用标记泄漏，检测才有意义）。</summary>
    private static bool HasTools(LlmOptions options) => options.Tools != null && options.Tools.Any();

    /// <summary>构造正文标记异常，message 附带截断的检出片段便于日志诊断。</summary>
    private static StrayToolCallMarkupException StrayMarkupException(string? content)
    {
        const int snippetLength = 80;
        var snippet = content ?? string.Empty;
        snippet = snippet.Length <= snippetLength ? snippet : snippet[..snippetLength] + "...";
        return new StrayToolCallMarkupException($"模型将工具调用以文本标记形式输出到正文：{snippet}");
    }

    private static StreamResetReason MapReason(LlmException e) => e switch
    {
        RateLimitException => StreamResetReason.RateLimited,
        ServerErrorException => StreamResetReason.ServerError,
        NetworkException => StreamResetReason.NetworkError,
        StrayToolCallMarkupException => StreamResetReason.StrayToolCallMarkup,
        _ => StreamResetReason.Other,
    };

    /// <summary>
    /// 单次尝试的 sink 包装（携带工具的请求）：增量即时透传；完成时对全量正文
    /// 做工具调用标记检测，命中则在转发 OnCompleted 之前抛出
    /// StrayToolCallMarkupException——穿透后端读循环，由重试循环捕获走 reset 重试。
    /// 不扣留增量：reset 语义下消费者会丢弃本段，扣留是不必要的复杂度。
    /// </summary>
    private sealed class MarkupGuardSink(IResettableStreamSink inner) : IStreamSink
    {
        public void OnTextDelta(string delta) => inner.OnTextDelta(delta);

        public void OnReasoningDelta(string delta) => inner.OnReasoningDelta(delta);

        public void OnCompleted(GenerateResponse response, TokenUsage usage)
        {
            if (StrayToolCallDetector.IsStrayToolCallMarkup(response.Content))
            {
                throw StrayMarkupException(response.Content);
            }
            inner.OnCompleted(response, usage);
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
