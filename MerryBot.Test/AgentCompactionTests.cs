using System.Runtime.CompilerServices;
using Agent;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// 验证上下文压缩阈值判断不会因多轮工具迭代累加 token 用量而虚高：
/// 上下文真实大小 = 最后一次请求的输入 tokens（覆盖语义，对齐 AniaBot LastPromptTokens），
/// 计费口径 totalUsage 仍按多轮累加。
/// </summary>
public class AgentCompactionTests
{
    [Fact]
    public async Task MultiIteration_DoesNotOvercountContext_AndSkipsCompaction()
    {
        var backend = new FakeBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var logEvents = new List<AgentLogEvent>();
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            ContextCompactRatio = 0.7, // tokenLimit=10000 → 阈值 7000
            MaxOutputTokens = 512,
            OnLog = e => logEvents.Add(e),
        };

        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { new FakeToolSet() });
        var (reply, totalUsage) = await agent.Chat("hi", CancellationToken.None);

        // 每轮输入恒为 6000（ratio 0.6 < 0.7）：两轮工具迭代后真实上下文仍为 6000，不触发压缩。
        // 旧实现（多轮累加）这里会累加成 12000 → ratio 1.2 ≥ 0.7 → 过早触发有损压缩。
        Assert.Equal(2, backend.RequestCount); // 恰好 2 次请求，无第三次压缩请求（Generate 若被调用会抛异常）
        Assert.DoesNotContain(logEvents, e => e.Kind == AgentLogEventKind.ContextCompaction);
        Assert.Equal("最终回复", reply);

        // 计费口径不受影响：两轮各 6000 → 累加 12000
        Assert.Equal(12000, totalUsage.totalUsage);
    }

    [Fact]
    public async Task CompactionRunsAfterToolLoop_NotBetweenToolCalls()
    {
        var backend = new CompactingBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var logEvents = new List<AgentLogEvent>();
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            ContextCompactRatio = 0.7, // tokenLimit=10000 → 阈值 7000；每轮 prompt 8000 > 7000
            MaxOutputTokens = 512,
            OnLog = e => logEvents.Add(e),
        };

        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { new FakeToolSet() });
        var (reply, totalUsage) = await agent.Chat("hi", CancellationToken.None);

        // 工具链期间不压缩：第一轮返回工具调用后直接进入第二轮，无压缩请求；
        // 第二轮拿到最终回复、工具调用循环结束后才统一压缩（Generate 被调用一次）。
        Assert.Equal(2, backend.StreamRequests);  // 2 轮对话 LLM 调用
        Assert.Equal(1, backend.CompactRequests); // 收尾 1 次压缩请求
        Assert.Contains(logEvents, e => e.Kind == AgentLogEventKind.ContextCompaction);
        Assert.Equal("最终回复", reply);
        Assert.Equal(16000, totalUsage.totalUsage);
    }

    [Fact]
    public async Task TokenUsedIncludesCompletion_PrefersEarlierCompaction()
    {
        var backend = new CompletionPushesThresholdBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var logEvents = new List<AgentLogEvent>();
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            ContextCompactRatio = 0.7, // tokenLimit=10000 → 阈值 7000
            MaxOutputTokens = 512,
            OnLog = e => logEvents.Add(e),
        };

        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { new FakeToolSet() });
        var (reply, totalUsage) = await agent.Chat("hi", CancellationToken.None);

        // prompt=5000（ratio 0.5 < 0.7）单独不会触发压缩；取 prompt+completion=8000（0.8 ≥ 0.7）→ 收尾压缩。
        Assert.Equal(2, backend.StreamRequests);
        Assert.Equal(1, backend.CompactRequests);
        Assert.Contains(logEvents, e => e.Kind == AgentLogEventKind.ContextCompaction);
        Assert.Equal("最终回复", reply);
    }

    /// <summary>
    /// 每轮 prompt=5000、completion=3000（total=8000）：prompt 单独不够阈值（0.5），
    /// 取 prompt+completion 后才触发压缩（0.8）；Generate 返回压缩摘要。
    /// </summary>
    private sealed class CompletionPushesThresholdBackend : Backend
    {
        public int StreamRequests;
        public int CompactRequests;

        public async IAsyncEnumerable<StreamEvent> GenerateStream(
            IList<Message> messages, string systemPrompt, LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests++;
            if (StreamRequests == 1)
            {
                yield return new StreamCompleted(
                    new GenerateResponse(null, new[] { new ToolCall("call_1", "fake_tool", "{}") }, null),
                    new TokenUsage(8000, 5000, 3000));
                yield break;
            }
            yield return new StreamCompleted(
                new GenerateResponse("最终回复", null, null),
                new TokenUsage(8000, 5000, 3000));
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
        {
            CompactRequests++;
            return Task.FromResult<(GenerateResponse, TokenUsage)>(
                (new GenerateResponse("对话摘要", null, null), new TokenUsage(500, 0, 500)));
        }
    }

    /// <summary>每轮 prompt=8000（tokenLimit=10000，ratio 0.8 ≥ 0.7）；Generate 返回压缩摘要（压缩请求计数）。</summary>
    private sealed class CompactingBackend : Backend
    {
        public int StreamRequests;
        public int CompactRequests;

        public async IAsyncEnumerable<StreamEvent> GenerateStream(
            IList<Message> messages, string systemPrompt, LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests++;
            if (StreamRequests == 1)
            {
                yield return new StreamCompleted(
                    new GenerateResponse(null, new[] { new ToolCall("call_1", "fake_tool", "{}") }, null),
                    new TokenUsage(8000, 8000, 0));
                yield break;
            }
            yield return new StreamCompleted(
                new GenerateResponse("最终回复", null, null),
                new TokenUsage(8000, 8000, 0));
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
        {
            CompactRequests++;
            return Task.FromResult<(GenerateResponse, TokenUsage)>(
                (new GenerateResponse("对话摘要", null, null), new TokenUsage(500, 0, 500)));
        }
    }

    /// <summary>固定流式后端：第 1 次请求返回工具调用，第 2 次返回纯文本结束；每次 usage 均为 6000。</summary>
    private sealed class FakeBackend : Backend
    {
        public int RequestCount;

        public async IAsyncEnumerable<StreamEvent> GenerateStream(
            IList<Message> messages, string systemPrompt, LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                yield return new StreamCompleted(
                    new GenerateResponse(null, new[] { new ToolCall("call_1", "fake_tool", "{}") }, null),
                    new TokenUsage(6000, 6000, 0));
                yield break;
            }
            yield return new StreamCompleted(
                new GenerateResponse("最终回复", null, null),
                new TokenUsage(6000, 6000, 0));
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
            => throw new NotSupportedException("压缩不应触发，Generate 不应被调用");
    }

    private sealed class FakeToolSet : ToolSet
    {
        public override IList<ToolDef> Tools() => new[]
        {
            new ToolDef
            {
                type = "function",
                function = new FunctionDef { name = "fake_tool", description = "测试工具" },
            },
        };

        public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
            => Task.FromResult("ok");

        public override string? Prompt() => null;
    }
}