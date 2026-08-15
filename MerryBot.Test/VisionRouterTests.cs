using System.Runtime.CompilerServices;
using Agent;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

/// <summary>
/// VisionRouter 多辅助视觉模型逐层 fallback 测试。
/// 通过假 Backend 注入真实 Client，控制每个"模型"的成功/失败/取消。
/// </summary>
public sealed class VisionRouterTests
{
    private sealed class FakeBackend : Backend
    {
        public int CallCount { get; private set; }

        public Func<Task<(GenerateResponse, TokenUsage)>> Handler { get; set; } =
            () => throw new InvalidOperationException("no handler");

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken,
            IList<Message> messages,
            string systemPrompt,
            LlmOptions options)
        {
            CallCount++;
            return Handler();
        }

        // VisionRouter 只走非流式 Generate，流式接口保持未实现即可
        public IAsyncEnumerable<StreamEvent> GenerateStream(
            IList<Message> messages,
            string systemPrompt,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private static Client MakeClient(FakeBackend backend)
        => new(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));

    private static (GenerateResponse, TokenUsage) Response(string content)
        => (new GenerateResponse(content, null, null), TokenUsage.Zero);

    private static readonly byte[] SampleImage = [1, 2, 3];

    [Fact]
    public async Task FirstModel_Fails_Automatically_Uses_Next()
    {
        var first = new FakeBackend { Handler = () => throw new InvalidOperationException("first down") };
        var second = new FakeBackend { Handler = () => Task.FromResult(Response("second description")) };
        var router = new VisionRouter(mainHasVision: false, [MakeClient(first), MakeClient(second)]);

        var result = await router.DescribeImageAsync(SampleImage, "image/png", null);

        Assert.Equal("second description", result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task FirstModel_Succeeds_Does_Not_Call_Next()
    {
        var first = new FakeBackend { Handler = () => Task.FromResult(Response("first description")) };
        var second = new FakeBackend { Handler = () => Task.FromResult(Response("second description")) };
        var router = new VisionRouter(mainHasVision: false, [MakeClient(first), MakeClient(second)]);

        var result = await router.DescribeImageAsync(SampleImage, "image/png", null);

        Assert.Equal("first description", result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task AllModels_Fail_Throws_With_Combined_Errors()
    {
        var first = new FakeBackend { Handler = () => throw new InvalidOperationException("boom-one") };
        var second = new FakeBackend { Handler = () => throw new InvalidOperationException("boom-two") };
        var router = new VisionRouter(mainHasVision: false, [MakeClient(first), MakeClient(second)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.DescribeImageAsync(SampleImage, "image/png", null));

        Assert.Contains("boom-one", ex.Message);
        Assert.Contains("boom-two", ex.Message);
        Assert.Contains("所有辅助视觉模型均失败", ex.Message);
    }

    [Fact]
    public async Task No_VisionClients_Throws_NotConfigured()
    {
        var router = new VisionRouter(mainHasVision: false, visionClients: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.DescribeImageAsync(SampleImage, "image/png", null));

        Assert.Contains("未配置", ex.Message);
    }

    [Fact]
    public async Task Cancellation_Propagates_Without_Fallback()
    {
        var first = new FakeBackend
        {
            Handler = () => Task.FromException<(GenerateResponse, TokenUsage)>(new OperationCanceledException("cancelled")),
        };
        var second = new FakeBackend { Handler = () => Task.FromResult(Response("second description")) };
        var router = new VisionRouter(mainHasVision: false, [MakeClient(first), MakeClient(second)]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => router.DescribeImageAsync(SampleImage, "image/png", null, cancellationToken: CancellationToken.None));

        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount); // 取消不降级
    }
}
