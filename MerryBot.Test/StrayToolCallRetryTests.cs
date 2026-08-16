using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// LlmClient 流式重试（reset 语义）与"模型把工具调用当正文输出"检测的测试。
/// 通过脚本化假 Backend 推送增量/异常，RecordingSink 记录事件序列。
/// 覆盖：检出标记（开头/结尾）后 reset 重试、预算耗尽抛异常、正文中间的标记
/// 提及不重试、无工具请求不检测、首元素前/中途断流 reset 重试、取消与不可重试
/// 异常不发 reset。
/// </summary>
public sealed class StrayToolCallRetryTests
{
    private static readonly LlmOptions WithTools = new(
        Tools: [new ToolDef { type = "function", function = new FunctionDef { name = "shell" } }]);

    private static readonly LlmOptions NoTools = new();

    private static Client CreateClient(FakeBackend backend)
        => new(backend, new ClientConfig(maxAttempt: 3, initialDelay: TimeSpan.Zero));

    private static async Task<RecordingSink> RunStreamAsync(Client client, LlmOptions options, CancellationToken ct = default)
    {
        var sink = new RecordingSink();
        await client.GenerateStream(sink, [], "", options, ct);
        return sink;
    }

    [Fact]
    public async Task StrayDsml_AtStart_ResetAndRetry()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("<|DSML|tool_calls>"),
            new ScriptText("<invoke name=\"shell\">"),
            new ScriptDone("<|DSML|tool_calls><invoke name=\"shell\">"));
        backend.EnqueueStream(
            new ScriptText("你好"),
            new ScriptText("，世界"),
            new ScriptDone("你好，世界"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        // 无扣留：第一段的增量原样推送给消费者，随后 reset 作废，第二段干净增量
        Assert.Equal(
            [("text", "<|DSML|tool_calls>"), ("text", "<invoke name=\"shell\">"),
             ("reset", nameof(StreamResetReason.StrayToolCallMarkup)),
             ("text", "你好"), ("text", "，世界"), ("done", "你好，世界")],
            sink.Events);
        Assert.Equal("你好，世界", sink.Response?.Content);
    }

    [Fact]
    public async Task StrayMarkup_AtTail_AfterCleanText_Detected()
    {
        // 先输出正常文本、结尾才吐出工具调用标记的模型行为
        const string stray = "好的，我来查一下。\n<|DSML|tool_calls><invoke name=\"shell\">"
            + "<parameter name=\"command\">ls</parameter></invoke>";
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("好的，我来查一下。\n"),
            new ScriptText("<|DSML|tool_calls><invoke name=\"shell\"><parameter name=\"command\">ls</parameter></invoke>"),
            new ScriptDone(stray));
        backend.EnqueueStream(new ScriptText("查完了"), new ScriptDone("查完了"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        var reset = Assert.Single(sink.Events, e => e.Kind == "reset");
        Assert.Equal(nameof(StreamResetReason.StrayToolCallMarkup), reset.Payload);
        Assert.Equal("查完了", sink.Response?.Content);
    }

    [Fact]
    public async Task StrayMarkup_BudgetExhausted_Throws()
    {
        var backend = new FakeBackend();
        for (int i = 0; i < 3; i++)
        {
            backend.EnqueueStream(
                new ScriptText("<|DSML|tool_calls>"),
                new ScriptDone("<|DSML|tool_calls>"));
        }

        var sink = new RecordingSink();
        var ex = await Assert.ThrowsAsync<StrayToolCallMarkupException>(async () =>
            await CreateClient(backend).GenerateStream(sink, [], "", WithTools));

        Assert.Equal(3, backend.StreamCalls);
        Assert.Null(sink.Response);
        // reset 仅在确定重试时回调：3 次尝试 = 2 次 reset，最后一次失败直接抛
        Assert.Equal(2, sink.Events.Count(e => e.Kind == "reset"));
        Assert.Contains("<|DSML|", ex.Message);
    }

    [Fact]
    public async Task MidContentMarkupMention_NotRetried()
    {
        // 标记出现在正文中间（开头与结尾都是正常文本）：视为合法提及，不重试
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("好的，<invoke name=\"shell\"> 这种写法只是举例，"),
            new ScriptText("我来说明一下。"),
            new ScriptDone("好的，<invoke name=\"shell\"> 这种写法只是举例，我来说明一下。"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(1, backend.StreamCalls);
        Assert.DoesNotContain(sink.Events, e => e.Kind == "reset");
        Assert.Equal("好的，<invoke name=\"shell\"> 这种写法只是举例，我来说明一下。", sink.Response?.Content);
    }

    [Fact]
    public async Task Markup_SplitAcrossChunks_StillDetected()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("<|DS"),
            new ScriptText("ML|tool_calls>"),
            new ScriptDone("<|DSML|tool_calls>"));
        backend.EnqueueStream(new ScriptText("干净回复"), new ScriptDone("干净回复"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        Assert.Equal("干净回复", sink.Response?.Content);
    }

    [Fact]
    public async Task JsonToolCallStructure_Detected()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("{\"name\": \"shell\","),
            new ScriptText(" \"arguments\": {\"command\": \"ls\"}}"),
            new ScriptDone("{\"name\": \"shell\", \"arguments\": {\"command\": \"ls\"}}"));
        backend.EnqueueStream(new ScriptText("正常文本"), new ScriptDone("正常文本"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        Assert.Equal("正常文本", sink.Response?.Content);
    }

    [Fact]
    public async Task NoTools_MarkupPassesThrough()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(
            new ScriptText("<|DSML|tool_calls>"),
            new ScriptDone("<|DSML|tool_calls>"));

        var sink = await RunStreamAsync(CreateClient(backend), NoTools);

        Assert.Equal(1, backend.StreamCalls);
        Assert.Equal("<|DSML|tool_calls>", sink.Response?.Content);
        Assert.DoesNotContain(sink.Events, e => e.Kind == "reset");
    }

    [Fact]
    public async Task RetryableError_BeforeFirstDelta_ResetsAndRetries()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(new NetworkException("connection reset"));
        backend.EnqueueStream(new ScriptText("恢复"), new ScriptDone("恢复"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        Assert.Equal("恢复", sink.Text);
        var reset = Assert.Single(sink.Events, e => e.Kind == "reset");
        Assert.Equal(nameof(StreamResetReason.NetworkError), reset.Payload);
    }

    [Fact]
    public async Task MidStreamError_ResetsAndRetries_EventOrder()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(new ScriptText("你好"), new NetworkException("boom"));
        backend.EnqueueStream(new ScriptText("恢复"), new ScriptDone("恢复"));

        var sink = await RunStreamAsync(CreateClient(backend), WithTools);

        Assert.Equal(2, backend.StreamCalls);
        // 完整事件顺序：第一段增量 → reset → 第二段增量 → done
        Assert.Equal(
            [("text", "你好"), ("reset", nameof(StreamResetReason.NetworkError)), ("text", "恢复"), ("done", "恢复")],
            sink.Events);
    }

    [Fact]
    public async Task MidStreamError_BudgetExhausted_ThrowsAfterResets()
    {
        var backend = new FakeBackend();
        for (int i = 0; i < 3; i++)
        {
            backend.EnqueueStream(new ScriptText("部分"), new NetworkException("boom"));
        }

        var sink = new RecordingSink();
        await Assert.ThrowsAsync<NetworkException>(async () =>
            await CreateClient(backend).GenerateStream(sink, [], "", WithTools));

        Assert.Equal(3, backend.StreamCalls);
        Assert.Equal(2, sink.Events.Count(e => e.Kind == "reset"));
        Assert.Null(sink.Response);
    }

    [Fact]
    public async Task NonRetryableError_Throws_WithoutReset()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(new AuthenticationException("bad key", 401));

        var sink = new RecordingSink();
        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await CreateClient(backend).GenerateStream(sink, [], "", WithTools));

        Assert.Equal(1, backend.StreamCalls);
        Assert.DoesNotContain(sink.Events, e => e.Kind == "reset");
    }

    [Fact]
    public async Task Cancellation_NoResetEvent_PropagatesOce()
    {
        var backend = new FakeBackend();
        backend.EnqueueStream(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sink = new RecordingSink();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CreateClient(backend).GenerateStream(sink, [], "", WithTools, cts.Token));

        Assert.Equal(1, backend.StreamCalls);
        Assert.DoesNotContain(sink.Events, e => e.Kind == "reset");
    }

    [Fact]
    public async Task Generate_StrayContent_RetriedOnce()
    {
        var backend = new FakeBackend();
        backend.EnqueueGenerate("<|DSML|tool_calls><invoke name=\"shell\">");
        backend.EnqueueGenerate("正常回复");

        var (response, _) = await CreateClient(backend)
            .Generate(TestContext.Current.CancellationToken, [], "", WithTools);

        Assert.Equal(2, backend.GenerateCalls);
        Assert.Equal("正常回复", response.Content);
    }

    [Fact]
    public async Task Generate_StrayContentTwice_Throws()
    {
        var backend = new FakeBackend();
        backend.EnqueueGenerate("<|DSML|tool_calls>");
        backend.EnqueueGenerate("<|DSML|tool_calls>");

        await Assert.ThrowsAsync<StrayToolCallMarkupException>(async () =>
            await CreateClient(backend).Generate(TestContext.Current.CancellationToken, [], "", WithTools));

        Assert.Equal(2, backend.GenerateCalls);
    }

    [Fact]
    public async Task Generate_NoTools_StrayContentPassesThrough()
    {
        var backend = new FakeBackend();
        backend.EnqueueGenerate("<|DSML|tool_calls>");

        var (response, _) = await CreateClient(backend)
            .Generate(TestContext.Current.CancellationToken, [], "", NoTools);

        Assert.Equal(1, backend.GenerateCalls);
        Assert.Equal("<|DSML|tool_calls>", response.Content);
    }

    // ---------- 检测器单元测试（静态全量检测：开头/结尾窗口） ----------

    [Fact]
    public void IsStrayToolCallMarkup_PrefixPatterns()
    {
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup("<|DSML|tool_calls><invoke name=\"shell\">"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup("  \n<invoke name=\"shell\">"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup("<tool_call>{\"name\":\"x\"}</tool_call>"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup("{\"name\": \"shell\", \"arguments\": {}}"));
    }

    [Fact]
    public void IsStrayToolCallMarkup_TailPatterns()
    {
        // 结尾的标记（先正常文本后吐工具调用）应检出
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup(
            "好的，我来查。\n<|DSML|tool_calls><invoke name=\"shell\"><parameter name=\"command\">ls</parameter></invoke>"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup(
            "查询结果如下。\n{\"name\": \"shell\", \"arguments\": {\"command\": \"ls\"}}"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup(
            "让我试试。<invoke name=\"shell\"></invoke>"));
        Assert.True(StrayToolCallDetector.IsStrayToolCallMarkup(
            "处理中。<tool_call>{\"name\":\"x\"}</tool_call>"));
    }

    [Fact]
    public void IsStrayToolCallMarkup_NegativeCases()
    {
        // 中间的标记提及（开头结尾均正常）不算泄漏
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup("普通正文 <invoke 出现在中间不算"));
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup("<3 爱你"));
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup("{\"result\": 1}"));
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup("<div>普通 HTML</div>"));
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup(null));
        Assert.False(StrayToolCallDetector.IsStrayToolCallMarkup(""));
    }

    // ---------- 测试基础设施 ----------

    /// <summary>脚本令牌：正文增量。</summary>
    private sealed record ScriptText(string Text);

    /// <summary>脚本令牌：流正常结束（携带全量正文）。</summary>
    private sealed record ScriptDone(string Content);

    /// <summary>
    /// 脚本化假后端：每次 Generate/GenerateStream 弹出下一段脚本；
    /// 流式脚本元素为 ScriptText/ScriptDone 或 Exception（抛出点）。
    /// </summary>
    private sealed class FakeBackend : Backend
    {
        private readonly Queue<object[]> _streamScripts = new();
        private readonly Queue<string?> _generateScripts = new();

        public int StreamCalls { get; private set; }
        public int GenerateCalls { get; private set; }

        public void EnqueueStream(params object[] script) => _streamScripts.Enqueue(script);

        public void EnqueueGenerate(string? content) => _generateScripts.Enqueue(content);

        public Task<(GenerateResponse, TokenUsage)> Generate(CancellationToken cancellationToken, IList<Message> messages, string systemPrompt, LlmOptions options)
        {
            GenerateCalls++;
            var content = _generateScripts.Dequeue();
            return Task.FromResult((new GenerateResponse(content, null, null), TokenUsage.Zero));
        }

        public Task GenerateStream(IStreamSink sink, IList<Message> messages, string systemPrompt, LlmOptions options, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            var script = _streamScripts.Dequeue();
            foreach (var item in script)
            {
                switch (item)
                {
                    case Exception ex:
                        throw ex;
                    case ScriptText text:
                        sink.OnTextDelta(text.Text);
                        break;
                    case ScriptDone done:
                        sink.OnCompleted(new GenerateResponse(done.Content, null, null), TokenUsage.Zero);
                        break;
                }
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>记录事件序列的消费端（不实现 reset 丢弃，原样记录便于断言顺序）。</summary>
    private sealed class RecordingSink : IResettableStreamSink
    {
        public List<(string Kind, string Payload)> Events { get; } = new();
        public GenerateResponse? Response { get; private set; }

        public string Text => string.Concat(Events.Where(e => e.Kind == "text").Select(e => e.Payload));

        public void OnTextDelta(string delta) => Events.Add(("text", delta));

        public void OnReasoningDelta(string delta) => Events.Add(("reasoning", delta));

        public void OnReset(StreamResetReason reason, Exception cause) => Events.Add(("reset", reason.ToString()));

        public void OnCompleted(GenerateResponse response, TokenUsage usage)
        {
            Events.Add(("done", response.Content ?? string.Empty));
            Response = response;
        }
    }
}
