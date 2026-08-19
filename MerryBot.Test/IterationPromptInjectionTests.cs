using Agent;
using LlmBackend;
using LlmClient;

namespace MerryBot.Test;

public sealed class IterationPromptInjectionTests
{
    [Fact]
    public void ToolSet_DefaultIterationPromptInjectionIsEmpty()
    {
        PromptToolSet toolSet = new("静态提示");

        Assert.Null(toolSet.IterationPromptInjection());
        Assert.Same(toolSet, toolSet.Copy());
    }

    [Fact]
    public async Task Chat_PrependsDynamicInjectionToUserInput_WithoutChangingSystemPrompt()
    {
        DynamicPromptToolSet toolSet = new();
        CapturingBackend backend = new();
        Client client = new(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        AgentOptions options = new() { SystemPrompt = "固定 system prompt" };
        Agent.Agent agent = await Agent.Agent.Create(null, client, 10_000, options, [toolSet]);

        await agent.Chat("第一次输入", CancellationToken.None);
        toolSet.State = "第二次状态";
        await agent.Chat("第二次输入", CancellationToken.None);

        Assert.Equal(2, backend.SystemPrompts.Count);
        Assert.All(backend.SystemPrompts, prompt => Assert.Equal("固定 system prompt", prompt));

        Message secondUserMessage = backend.Requests[1]
            .Last(message => message.role.Value == "user");
        string secondUserText = string.Concat(secondUserMessage.content.OfType<MessagePartText>().Select(part => part.text));
        Assert.StartsWith(
            $"<DYNAMIC>第二次状态</DYNAMIC>{Environment.NewLine}{Environment.NewLine}第二次输入",
            secondUserText);
        Assert.DoesNotContain("<DYNAMIC>第二次状态</DYNAMIC>",
            string.Concat(backend.Requests[0].SelectMany(message => message.content.OfType<MessagePartText>()).Select(part => part.text)));
    }

    [Fact]
    public async Task ResetAsync_ResetsToolSetState()
    {
        DynamicPromptToolSet toolSet = new() { State = "changed" };
        CapturingBackend backend = new();
        Client client = new(backend, new ClientConfig(maxAttempt: 1, initialDelay: TimeSpan.Zero));
        Agent.Agent agent = await Agent.Agent.Create(null, client, 10_000, new AgentOptions(), [toolSet]);

        await agent.ResetAsync();

        Assert.Equal("reset", toolSet.State);
    }

    private sealed class DynamicPromptToolSet : ToolSet
    {
        public string State { get; set; } = "initial";

        public override IList<ToolDef> Tools() => [];

        public override Task<string> InvokeAsync(
            CancellationToken cancellationToken,
            ToolCall toolCall,
            Action<Message> onIterationAdd)
            => throw new NotSupportedException();

        public override string? Prompt() => null;

        public override string? IterationPromptInjection() => $"<DYNAMIC>{State}</DYNAMIC>";

        public override void Reset() => State = "reset";
    }

    private sealed class CapturingBackend : Backend
    {
        public List<string> SystemPrompts { get; } = [];
        public List<IList<Message>> Requests { get; } = [];

        public Task GenerateStream(
            IStreamSink sink,
            IList<Message> messages,
            string systemPrompt,
            LlmOptions options,
            CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            Requests.Add(messages.ToList());
            sink.OnCompleted(new GenerateResponse("完成", null, null), TokenUsage.Zero);
            return Task.CompletedTask;
        }

        public Task<(GenerateResponse, TokenUsage)> Generate(
            CancellationToken cancellationToken,
            IList<Message> messages,
            string systemPrompt,
            LlmOptions options)
            => throw new NotSupportedException("测试不应触发上下文压缩");
    }
}
