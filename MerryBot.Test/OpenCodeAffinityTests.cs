using System.Net;
using System.Text;
using LlmBackend;

namespace MerryBot.Test;

/// <summary>
/// OpenCode 会话亲和头（x-opencode-session）决议与发送测试，对应上游 hermes-agent #101864。
/// </summary>
public sealed class OpenCodeAffinityTests
{
    [Theory]
    [InlineData("https://opencode.ai/zen/v1")]
    [InlineData("https://opencode.ai/zen/go/v1")]
    [InlineData("HTTPS://OPENCODE.AI/zen/v1")]
    [InlineData("https://foo.opencode.ai/bar")]
    [InlineData("http://opencode.ai:8080/x")]
    public void IsOpenCodeTarget_MatchesOpenCodeHosts(string baseUrl)
    {
        Assert.True(OpenCodeAffinity.IsOpenCodeTarget(baseUrl));
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://opencode.ai.evil.com/")]
    [InlineData("https://notopencode.ai/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    public void IsOpenCodeTarget_RejectsNonOpenCodeTargets(string? baseUrl)
    {
        Assert.False(OpenCodeAffinity.IsOpenCodeTarget(baseUrl));
    }

    [Fact]
    public void ResolveSessionKey_ReturnsConfiguredKeyVerbatim()
    {
        Assert.Equal(
            "sess-affinity-1",
            OpenCodeAffinity.ResolveSessionKey("sess-affinity-1", "https://opencode.ai/zen/v1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSessionKey_GeneratesStableValueWhenMissing(string? configured)
    {
        string? key = OpenCodeAffinity.ResolveSessionKey(configured, "https://opencode.ai/zen/go/v1");
        Assert.False(string.IsNullOrEmpty(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sess-affinity-1")]
    public void ResolveSessionKey_ReturnsNullForNonOpenCodeTargets(string? configured)
    {
        Assert.Null(OpenCodeAffinity.ResolveSessionKey(configured, "https://openrouter.ai/api/v1"));
    }

    [Fact]
    public void Backends_UseConfiguredSessionKeyVerbatim()
    {
        const string baseUrl = "https://opencode.ai/zen/v1";
        Assert.Equal("sess-affinity-1", new ChatCompletionBackend(baseUrl, "k", "m", "sess-affinity-1").SessionKey);
        Assert.Equal("sess-affinity-1", new ResponsesBackend(baseUrl, "k", "m", "sess-affinity-1").SessionKey);
        Assert.Equal("sess-affinity-1", new AnthropicBackend(baseUrl, "k", "m", sessionKey: "sess-affinity-1").SessionKey);
    }

    [Fact]
    public void Backends_MaintainStableRandomKeyWhenMissing()
    {
        const string baseUrl = "https://opencode.ai/zen/v1";
        var chat = new ChatCompletionBackend(baseUrl, "k", "m");
        var responses = new ResponsesBackend(baseUrl, "k", "m");
        var anthropic = new AnthropicBackend(baseUrl, "k", "m");

        // 同一实例多次读取稳定（构造期生成一次，跨请求/重试不变）
        Assert.False(string.IsNullOrEmpty(chat.SessionKey));
        Assert.Equal(chat.SessionKey, chat.SessionKey);
        Assert.False(string.IsNullOrEmpty(responses.SessionKey));
        Assert.False(string.IsNullOrEmpty(anthropic.SessionKey));
        // 不同实例各自独立随机
        Assert.NotEqual(chat.SessionKey, responses.SessionKey);
    }

    [Fact]
    public void Backends_HaveNoSessionKeyForNonOpenCodeTargets()
    {
        const string baseUrl = "https://openrouter.ai/api/v1";
        Assert.Null(new ChatCompletionBackend(baseUrl, "k", "m", "sess-affinity-1").SessionKey);
        Assert.Null(new ResponsesBackend(baseUrl, "k", "m").SessionKey);
        Assert.Null(new AnthropicBackend(baseUrl, "k", "m").SessionKey);
    }

    [Fact]
    public void ApplySessionHeader_DoesNotOverrideExistingHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions");
        request.Headers.TryAddWithoutValidation(OpenCodeAffinity.SessionHeaderName, "pinned");
        OpenCodeAffinity.ApplySessionHeader(request, "sess-affinity-1");
        Assert.Equal("pinned", string.Join(",", request.Headers.GetValues(OpenCodeAffinity.SessionHeaderName)));
    }

    [Fact]
    public async Task Generate_DoesNotSendSessionHeaderToNonOpenCodeTargets()
    {
        string? capturedHeader = await CaptureChatCompletionHeaderAsync(stream: false);
        Assert.Null(capturedHeader);
    }

    [Fact]
    public async Task GenerateStream_DoesNotSendSessionHeaderToNonOpenCodeTargets()
    {
        string? capturedHeader = await CaptureChatCompletionHeaderAsync(stream: true);
        Assert.Null(capturedHeader);
    }

    /// <summary>
    /// 在回环地址起一个最小 ChatCompletion 服务端，捕获 x-opencode-session 请求头。
    /// 回环地址不是 OpenCode 目标，期望头缺席（门控语义的 wire 级证明）。
    /// </summary>
    private static async Task<string?> CaptureChatCompletionHeaderAsync(bool stream)
    {
        using TcpListenerProbe probe = new();
        int port = probe.Port;
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string? capturedHeader = null;
        Task server = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync();
            capturedHeader = context.Request.Headers[OpenCodeAffinity.SessionHeaderName];
            context.Response.StatusCode = 200;
            await using Stream output = context.Response.OutputStream;
            if (stream)
            {
                context.Response.ContentType = "text/event-stream";
                byte[] chunk = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");
                await output.WriteAsync(chunk);
            }
            else
            {
                context.Response.ContentType = "application/json";
                byte[] body = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}");
                await output.WriteAsync(body);
            }
        });

        try
        {
            ChatCompletionBackend backend = new($"http://127.0.0.1:{port}", "test-key", "test-model", "sess-affinity-1");
            if (stream)
            {
                RecordingSink sink = new();
                await backend.GenerateStream(sink, [Message.User("test")], string.Empty, new LlmOptions(), CancellationToken.None);
            }
            else
            {
                await backend.Generate(CancellationToken.None, [Message.User("test")], string.Empty, new LlmOptions());
            }
            return capturedHeader;
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private sealed class TcpListenerProbe : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;

        public TcpListenerProbe()
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _listener.Stop();
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();
    }

    private sealed class RecordingSink : IStreamSink
    {
        public void OnTextDelta(string delta) { }

        public void OnReasoningDelta(string delta) { }

        public void OnCompleted(GenerateResponse response, TokenUsage usage) { }
    }
}
