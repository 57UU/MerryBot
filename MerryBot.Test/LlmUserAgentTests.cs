using System.Net.Http;
using LlmBackend;

namespace MerryBot.Test;

/// <summary>
/// LLM 请求客户端标识头（User-Agent: MerryBot/1.0）测试。
/// </summary>
public sealed class LlmUserAgentTests
{
    [Fact]
    public void ApplyUserAgent_AddsMerryBotToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");

        LlmUserAgent.ApplyUserAgent(request);

        Assert.Equal(LlmDefaults.UserAgent, request.Headers.UserAgent.ToString());
    }

    [Fact]
    public void ApplyUserAgent_DoesNotOverrideExplicitValue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.UserAgent.ParseAdd("CustomBot/2.0");

        LlmUserAgent.ApplyUserAgent(request);

        Assert.Equal("CustomBot/2.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public void ApplyUserAgent_IsIdempotent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");

        LlmUserAgent.ApplyUserAgent(request);
        LlmUserAgent.ApplyUserAgent(request);

        Assert.Equal(LlmDefaults.UserAgent, request.Headers.UserAgent.ToString());
    }
}
