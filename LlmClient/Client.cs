using System.Runtime.CompilerServices;
using LlmBackend;

namespace LlmClient;

/// <summary>
/// LLM 客户端，封装 Backend 调用并实现重试：优先使用异常携带的避让时间
/// （RateLimitException.RetryAfter），否则按 initialDelay 指数退避。
/// 后端引用可运行时替换（<see cref="UpdateBackend"/>），切换在请求间隙生效——
/// 每次 Generate/GenerateStream 开始时读取当前引用快照，同一请求全程用同一后端。
/// </summary>
public class Client
{
    private readonly object _sync = new();
    private Backend backend;
    private readonly ClientConfig clientConfig;

    public Client(Backend backend, ClientConfig clientConfig)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.clientConfig = clientConfig;
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
    /// </summary>
    public async Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await CurrentBackend.Generate(cancellationToken, messages, systemPrompt, options);
            }
            catch (LlmException e) when (e.Retryable && attempt < clientConfig.maxAttempt)
            {
                TimeSpan delay = GetDelay(e, attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 流式调用后端生成回复，最多尝试 maxAttempt 次。
    /// 仅重试首元素产出前的可重试异常（限速/服务器错误/网络错误）：流一旦开始产出
    /// 便无法透明重试（消费者会看到重复文本），中途失败直接抛出。
    /// 迭代器转发使 [EnumeratorCancellation] 生效：消费方 WithCancellation(ct) 的
    /// 取消令牌经枚举器通道绑定到本参数，最终传给后端与重试等待。
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> GenerateStream(
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = new RetryableStream(this, messages, systemPrompt, options, cancellationToken);
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield return enumerator.Current;
        }
    }

    /// <summary>
    /// 重试状态机：外层 MoveNextAsync 失败时，仅当"尚未产出首元素"且还有重试次数才
    /// 重建后端枚举器；成功后外层继续使用同一枚举器（重试对消费者完全透明）。
    /// </summary>
    private sealed class RetryableStream : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        private readonly Client _client;
        private readonly IList<Message> _messages;
        private readonly string _systemPrompt;
        private readonly LlmOptions _options;
        private readonly CancellationToken _cancellationToken;

        private IAsyncEnumerator<StreamEvent>? _inner;
        private int _attempt;
        private bool _started;

        public RetryableStream(Client client, IList<Message> messages, string systemPrompt, LlmOptions options, CancellationToken cancellationToken)
        {
            _client = client;
            _messages = messages;
            _systemPrompt = systemPrompt;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public StreamEvent Current => _inner!.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (true)
            {
                _inner ??= _client.CurrentBackend
                    .GenerateStream(_messages, _systemPrompt, _options)
                    .GetAsyncEnumerator(_cancellationToken);
                try
                {
                    if (await _inner.MoveNextAsync())
                    {
                        _started = true;
                        return true;
                    }
                    // 流正常结束：交还外层，由消费者随后 DisposeAsync 释放
                    return false;
                }
                catch (LlmException e) when (e.Retryable && !_started && _attempt + 1 < _client.clientConfig.maxAttempt)
                {
                    await _inner.DisposeAsync();
                    _inner = null;
                    _attempt++;
                    await Task.Delay(_client.GetDelay(e, _attempt), _cancellationToken);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            var inner = _inner;
            _inner = null;
            return inner?.DisposeAsync() ?? ValueTask.CompletedTask;
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
