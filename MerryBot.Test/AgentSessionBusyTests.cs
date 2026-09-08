using Agent;
using Agent.Session;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// AgentSessionManager 忙闲查询：IsSessionBusy / TryGetLiveSession。
/// 支撑 WebUI 会话快照页"清除会话"按钮的置灰逻辑：正处理消息时拒绝清除。
/// </summary>
public sealed class AgentSessionBusyTests
{
    [Fact]
    public async Task IsSessionBusy_ReflectsInFlightChat()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedBackend backend = new(gate.Task);
        Client client = new(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        int creations = 0;
        using AgentSessionManager manager = new(
            async id =>
            {
                Interlocked.Increment(ref creations);
                Agent.Agent agent = await Agent.Agent.Create(
                    null, client, tokenLimit: 10000, new AgentOptions { SystemPrompt = "test" }, Array.Empty<ToolSet>());
                return (agent, _ => { });
            });

        const string sessionId = "qq/group/123";

        // 未知会话：不忙，且查询本身不会创建会话
        Assert.False(manager.IsSessionBusy(sessionId));
        Assert.False(manager.TryGetLiveSession(sessionId, out _));
        Assert.Equal(0, creations);

        AgentSession session = await manager.GetSessionAsync(sessionId);
        Assert.False(manager.IsSessionBusy(sessionId));

        // 投递一条消息（backend 挂起 → 保持处理中）
        Task<(string Result, TokenUsage Usage)> pending = session.ChatAndWaitAsync("hi", _ => { });
        await SpinUntilAsync(() => manager.IsSessionBusy(sessionId), shouldBe: true);
        Assert.True(manager.IsSessionBusy(sessionId));
        Assert.True(manager.TryGetLiveSession(sessionId, out AgentSession? live) && ReferenceEquals(session, live));

        gate.TrySetResult();
        (string reply, _) = await pending;
        Assert.Equal("done", reply);

        // 处理完成后恢复空闲
        await SpinUntilAsync(() => manager.IsSessionBusy(sessionId), shouldBe: false);
        Assert.False(manager.IsSessionBusy(sessionId));
    }

    [Fact]
    public async Task TryGetLiveSession_DoesNotCreateSession()
    {
        Client client = new(
            new GatedBackend(Task.CompletedTask),
            new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        int creations = 0;
        using AgentSessionManager manager = new(
            async id =>
            {
                Interlocked.Increment(ref creations);
                Agent.Agent agent = await Agent.Agent.Create(
                    null, client, tokenLimit: 10000, new AgentOptions { SystemPrompt = "test" }, Array.Empty<ToolSet>());
                return (agent, _ => { });
            });

        Assert.False(manager.TryGetLiveSession("qq/group/999", out AgentSession? session));
        Assert.Null(session);
        Assert.Equal(0, creations);
    }

    private static async Task SpinUntilAsync(Func<bool> condition, bool shouldBe)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (condition() != shouldBe)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"忙闲状态未在 5 秒内变为 {shouldBe}");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>首轮请求挂起在门闩上，用于制造"处理中"窗口；放行后返回纯文本结束。</summary>
    private sealed class GatedBackend : Backend
    {
        private readonly Task _gate;

        public GatedBackend(Task gate)
        {
            _gate = gate;
        }

        public async Task GenerateStream(
            IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options,
            CancellationToken cancellationToken = default)
        {
            await _gate;
            sink.OnCompleted(new GenerateResponse("done", null, null), TokenUsage.Zero);
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
            => throw new NotSupportedException("测试只走流式路径");
    }
}
