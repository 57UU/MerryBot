using LlmBackend;

namespace MerryBot.Test;

/// <summary>
/// ChatCompletion 流式 SSE data 帧解析测试（纯函数，无网络）。
/// 覆盖正文/推理增量、工具调用分片、usage 块、[DONE]、错误帧与损坏帧。
/// </summary>
public sealed class LlmBackendStreamTests
{
    [Fact]
    public void TextDelta_Chunk_Parses_Text()
    {
        var chunk = ChatCompletionBackend.ParseChunk(
            """{"choices":[{"delta":{"content":"Hello"},"index":0}]}""");

        Assert.Null(chunk.Error);
        Assert.False(chunk.Done);
        Assert.Equal("Hello", chunk.Text);
        Assert.Null(chunk.Reasoning);
        Assert.Null(chunk.ToolCallParts);
    }

    [Fact]
    public void ReasoningContent_Chunk_Parses_Reasoning()
    {
        var chunk = ChatCompletionBackend.ParseChunk(
            """{"choices":[{"delta":{"reasoning_content":"think step"},"index":0}]}""");

        Assert.Equal("think step", chunk.Reasoning);
        Assert.Null(chunk.Text);
    }

    [Fact]
    public void ToolCall_Delta_Parts_Are_Exposed_By_Index()
    {
        var chunk = ChatCompletionBackend.ParseChunk(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":"{\"city\":"}}]},"index":0}]}""");

        Assert.NotNull(chunk.ToolCallParts);
        var part = Assert.Single(chunk.ToolCallParts);
        Assert.Equal(0, part.Index);
        Assert.Equal("call_1", part.Id);
        Assert.Equal("get_weather", part.Name);
        Assert.Equal("""{"city":""", part.Arguments);
    }

    [Fact]
    public void Usage_Only_Chunk_Parses_Usage()
    {
        var chunk = ChatCompletionBackend.ParseChunk(
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"prompt_cache_hit_tokens":2}}""");

        Assert.NotNull(chunk.Usage);
        Assert.Equal(10, chunk.Usage.PromptTokens);
        Assert.Equal(5, chunk.Usage.CompletionTokens);
        Assert.Equal(15, chunk.Usage.TotalTokens);
        Assert.Equal(2, chunk.Usage.CachedTokens);
        Assert.Null(chunk.Text);
    }

    [Fact]
    public void Done_Marker_Sets_Done_Flag()
    {
        var chunk = ChatCompletionBackend.ParseChunk("[DONE]");

        Assert.True(chunk.Done);
        Assert.Null(chunk.Error);
    }

    [Fact]
    public void Error_Envelope_Surfaces_Message()
    {
        var chunk = ChatCompletionBackend.ParseChunk(
            """{"error":{"message":"rate limit exceeded"}}""");

        Assert.Contains("rate limit exceeded", chunk.Error);
    }

    [Fact]
    public void Malformed_Json_Reports_Error()
    {
        var chunk = ChatCompletionBackend.ParseChunk("not json at all");

        Assert.NotNull(chunk.Error);
        Assert.Contains("无法解析", chunk.Error);
    }
}
