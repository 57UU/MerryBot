using LlmBackend;

namespace Agent.Tui;

/// <summary>
/// 可在运行时切换 baseUrl/apiKey/model 的 <see cref="Backend"/> 实现。
/// 每次请求按当前配置即时构造一个 <see cref="ChatCompletionBackend"/> 委托执行
/// （其 HttpClient 为 static，构造开销极低），从而无需重建 <c>Client</c>/<c>Agent</c>
/// 即可热切换供应商或模型。
/// </summary>
public sealed class DynamicBackend : Backend
{
    private readonly object _sync = new();
    private string _baseUrl;
    private string _apiKey;
    private string? _model;

    public DynamicBackend(string baseUrl, string apiKey, string? model)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = model;
    }

    /// <summary>当前配置快照（线程安全）。</summary>
    public (string BaseUrl, string ApiKey, string? Model) Current
    {
        get
        {
            lock (_sync) { return (_baseUrl, _apiKey, _model); }
        }
    }

    /// <summary>替换当前供应商/模型配置；下一次 <see cref="Generate"/> 即生效。</summary>
    public void Update(string baseUrl, string apiKey, string? model)
    {
        lock (_sync)
        {
            _baseUrl = baseUrl;
            _apiKey = apiKey;
            _model = model;
        }
    }

    public async Task<(GenerateResponse, TokenUsage)> Generate(
        CancellationToken cancellationToken,
        IList<Message> messages,
        string systemPrompt,
        LlmOptions options)
    {
        string baseUrl;
        string apiKey;
        string? model;
        lock (_sync) { baseUrl = _baseUrl; apiKey = _apiKey; model = _model; }

        // 读快照在锁内完成、不持锁 await，保证切换时并发请求看到一致配置。
        var inner = new ChatCompletionBackend(baseUrl, apiKey, model);
        return await inner.Generate(cancellationToken, messages, systemPrompt, options);
    }
}
