using System.Collections.Concurrent;
using Agent;
using Agent.Tools;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// 验证 Issue #7 修复：LLM 驱动的资源/成本失控防护。
/// ① 单轮并发工具调用受 MaxConcurrentToolCalls 上限约束（超限排队串行）；
/// ② 运行中的子 Agent 任务数受 maxSubagents 上限约束（超限拒绝派发）。
/// 直接走真实 ToolSetBridge 注册路径，最贴近生产行为。
/// </summary>
public sealed class AgentConcurrencyLimitTests
{
    [Fact]
    public async Task MaxConcurrentToolCalls_LimitsParallelToolExecution()
    {
        var backend = new FourToolCallsBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var logEvents = new List<AgentLogEvent>();
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            MaxConcurrentToolCalls = 2, // 上限 2
            OnLog = e => logEvents.Add(e),
        };

        // 工具执行时统计并发峰值：并发上限应被限制在 2
        var trackingToolSet = new ConcurrentTrackingToolSet();
        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { trackingToolSet });
        var (reply, _) = await agent.Chat("hi", CancellationToken.None);

        // 4 个工具调用全部执行完成
        Assert.Equal(4, logEvents.Count(e => e.Kind == AgentLogEventKind.ToolCallCompleted));
        // 峰值并发 ≤ 上限（2）
        Assert.InRange(trackingToolSet.PeakConcurrency, 1, 2);
        // 模型拿到全部结果后正常收尾
        Assert.Equal("最终回复", reply);
    }

    [Fact]
    public async Task MaxConcurrentToolCalls_OneMeansFullySerial()
    {
        var backend = new FourToolCallsBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 4,
            MaxConcurrentToolCalls = 1, // 全串行
        };

        var trackingToolSet = new ConcurrentTrackingToolSet();
        var agent = await Agent.Agent.Create(null, client, tokenLimit: 10000, options, new ToolSet[] { trackingToolSet });
        var (reply, _) = await agent.Chat("hi", CancellationToken.None);

        Assert.Equal(4, trackingToolSet.TotalInvocations);
        Assert.Equal(1, trackingToolSet.PeakConcurrency); // 永不并发
        Assert.Equal("最终回复", reply);
    }

    [Fact]
    public async Task SubAgentToolSet_RejectsNewSubagentsAtLimit()
    {
        // 子任务会真实创建子 Agent 并跑一轮 LLM；用挂起 backend 让子任务保持"运行中"，
        // 从而验证运行中数量上限生效
        var backend = new PendingBackend();
        var client = new Client(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        var options = new AgentOptions
        {
            SystemPrompt = "test",
            MaxIterations = 2,
            MaxConcurrentToolCalls = 2,
        };

        using var toolSet = new SubAgentToolSet(
            client,
            tokenLimit: 10000,
            options,
            tools: Array.Empty<ToolSet>(),
            notifyAsync: (_, _) => Task.CompletedTask,
            withdrawAsync: _ => Task.CompletedTask,
            shutdownToken: CancellationToken.None,
            maxSubagents: 1);

        // 第 1 个子任务：启动成功（backend 挂起 → 保持运行中）
        var first = await toolSet.InvokeAsync(
            CancellationToken.None,
            new ToolCall("call_1", "subagent", """{"task":"任务一","system_prompt":"你是助手"}"""),
            _ => { });
        Assert.Contains("task_id", first);
        Assert.DoesNotContain("已达上限", first);
        var startedId = first.Split("task_id: ")[1].Split('\n')[0].Trim();

        // 第 2 个子任务：运行中已达上限（1/1）→ 拒绝
        var second = await toolSet.InvokeAsync(
            CancellationToken.None,
            new ToolCall("call_2", "subagent", """{"task":"任务二","system_prompt":"你是助手"}"""),
            _ => { });
        Assert.Contains("已达上限", second);

        // 其余工具（subagent_output/subagent_stop）不受影响，可正常查询运行中的任务
        var query = await toolSet.InvokeAsync(
            CancellationToken.None,
            new ToolCall("call_3", "subagent_output", $$"""{"task_id":"{{startedId}}"}"""),
            _ => { });
        Assert.Contains("执行中", query);
    }

    /// <summary>执行工具时统计当前并发数与峰值并发，返回结果带序号。</summary>
    private sealed class ConcurrentTrackingToolSet : ToolSet
    {
        private readonly object _lock = new();
        private int _active;
        private int _peak;
        private int _count;

        public int PeakConcurrency { get { lock (_lock) return _peak; } }
        public int TotalInvocations { get { lock (_lock) return _count; } }

        public override IList<ToolDef> Tools() => new[]
        {
            new ToolDef
            {
                type = "function",
                function = new FunctionDef { name = "tracked_tool", description = "测试工具" },
            },
        };

        public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
        {
            int current;
            lock (_lock)
            {
                current = ++_active;
                _count++;
                if (current > _peak) _peak = current;
            }
            // 模拟真实工具耗时，让并发窗口可被观测到
            Thread.Sleep(50);
            lock (_lock)
            {
                _active--;
            }
            return Task.FromResult($"ok-{current}");
        }

        public override string? Prompt() => null;
    }

    /// <summary>第一次请求返回 4 个并发工具调用，第二次返回纯文本结束。</summary>
    private sealed class FourToolCallsBackend : Backend
    {
        private int _requests;

        public Task GenerateStream(
            IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options,
            CancellationToken cancellationToken = default)
        {
            _requests++;
            if (_requests == 1)
            {
                sink.OnCompleted(
                    new GenerateResponse(null,
                        new[]
                        {
                            new ToolCall("call_1", "tracked_tool", "{}"),
                            new ToolCall("call_2", "tracked_tool", "{}"),
                            new ToolCall("call_3", "tracked_tool", "{}"),
                            new ToolCall("call_4", "tracked_tool", "{}"),
                        },
                        null),
                    TokenUsage.Zero);
                return Task.CompletedTask;
            }
            sink.OnCompleted(new GenerateResponse("最终回复", null, null), TokenUsage.Zero);
            return Task.CompletedTask;
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
            => throw new NotSupportedException("压缩不应触发");
    }

    /// <summary>生成永不完成：用于让子任务保持"运行中"状态，验证上限。</summary>
    private sealed class PendingBackend : Backend
    {
        public Task GenerateStream(
            IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options,
            CancellationToken cancellationToken = default)
            => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
            => new TaskCompletionSource<(GenerateResponse, TokenUsage)>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}
