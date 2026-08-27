using Agent;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// 验证 Issue 1 修复：工具执行异常不再被 ToolSetBridge 吞成 {"error":...} 成功结果，
/// 而是上抛到 Agent.InvokeToolAsync，统一记录 ToolCallFailed（携带原始 Exception）并回填相同的
/// error JSON（模型仍可自纠重试），从而 TUI 不再误显"已完成"。
/// 直接走真实的 ToolSetBridge 注册路径（web_fetch 等内置工具即由此注册），最贴近原 bug。
/// </summary>
public sealed class ToolSetFailureTests
{
    [Fact]
    public async Task ToolThrows_AgentLogsToolCallFailed_NotToolCallCompleted()
    {
        var backend = new BoomBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var logEvents = new List<AgentLogEvent>();
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            OnLog = e => logEvents.Add(e),
        };

        // 通过真实的 ToolSetBridge 注册一个会抛异常的 "boom" 工具（复刻 web_fetch 等的注册方式）
        var toolSet = new ToolSetBridge.Builder()
            .AddFunction<BoomArgs>("boom", "always throws", _ => throw new InvalidOperationException("by-design failure"))
            .Build();

        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { toolSet });
        var (reply, _) = await agent.Chat("hi", CancellationToken.None);

        // 关键回归点：异常被上抛后记录"失败"（携带原始异常），而非被吞成"完成"
        var failed = Assert.Single(logEvents, e => e.Kind == AgentLogEventKind.ToolCallFailed);
        Assert.Equal("boom", failed.ToolName);
        Assert.IsType<InvalidOperationException>(failed.Exception);
        Assert.Equal("by-design failure", failed.Exception!.Message);
        Assert.DoesNotContain(logEvents, e => e.Kind == AgentLogEventKind.ToolCallCompleted);

        // 错误 JSON 回填到模型（模型可自纠）：第二轮请求中包含一条 tool 角色消息，正文为 error JSON
        Assert.NotNull(backend.SecondRequestMessages);
        var toolMessage = Assert.Single(
            backend.SecondRequestMessages!, m => m.role.Value == "tool");
        var toolText = string.Concat(toolMessage.content.OfType<MessagePartText>().Select(t => t.text));
        Assert.Contains("\"error\"", toolText);
        Assert.Contains("by-design failure", toolText);
        Assert.DoesNotContain("\\u", toolText, StringComparison.OrdinalIgnoreCase);

        // 工具失败后 error JSON 回填，模型返回最终回复，对话正常收尾
        Assert.Equal("最终回复", reply);
    }

    private sealed record BoomArgs;

    /// <summary>第 1 次请求让模型调用 boom 工具，第 2 次返回纯文本结束；并捕获第 2 次请求的消息列表。</summary>
    private sealed class BoomBackend : Backend
    {
        private int _requests;
        private IList<Message>? _secondRequestMessages;

        public Task GenerateStream(
            IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options,
            CancellationToken cancellationToken = default)
        {
            _requests++;
            if (_requests == 1)
            {
                sink.OnCompleted(
                    new GenerateResponse(null, new[] { new ToolCall("call_1", "boom", "{}") }, null),
                    TokenUsage.Zero);
                return Task.CompletedTask;
            }

            _secondRequestMessages = messages;
            sink.OnCompleted(new GenerateResponse("最终回复", null, null), TokenUsage.Zero);
            return Task.CompletedTask;
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
            => throw new NotSupportedException("压缩不应触发");

        public IList<Message>? SecondRequestMessages => _secondRequestMessages;
    }
}
