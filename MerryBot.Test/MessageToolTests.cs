using BotPlugin;
using BrowserService;
using Agent;
using LlmBackend;
using NapcatClient.MessageType;

namespace MerryBot.Test;

public sealed class MessageToolTests
{
    [Fact]
    public async Task GetMessage_MessageReference_ReadsMessage()
    {
        const long groupId = 123;
        const long messageId = 456;
        ProcessedMessage expected = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            messageId,
            789,
            "昵称",
            string.Empty,
            string.Empty,
            [TextData.FromText("价格 10 元")],
            DateTime.UtcNow,
            false);
        StubMessageService messageService = new() { MessageResult = expected };
        MessageTool tool = CreateTool(messageService, groupId);
        string reference = LocalMessageReference.Message(groupId, messageId);

        string result = await InvokeAsync(tool, "get_message", $"{{\"messageUrl\":\"{reference}\"}}");

        Assert.Contains("价格 10 元", result);
        Assert.Equal(groupId, messageService.MessageGroupId);
        Assert.Equal(reference, messageService.MessageReference);
        Assert.DoesNotContain("get_forward", tool.Tools().Select(static item => item.function?.name));
        Assert.DoesNotContain("get_refer", tool.Tools().Select(static item => item.function?.name));
    }

    [Fact]
    public async Task GetMessage_ForwardReference_ReadsForward()
    {
        const long groupId = 123;
        string reference = LocalMessageReference.Forward("forward-id");
        ProcessedMessage message = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            456,
            789,
            "昵称",
            string.Empty,
            string.Empty,
            [TextData.FromText("报价 20 元")],
            DateTime.UtcNow,
            false);
        StubMessageService messageService = new()
        {
            ForwardResult = new ProcessedForwardMessage(reference, groupId, [message], DateTime.UtcNow),
        };
        MessageTool tool = CreateTool(messageService, groupId);

        string result = await InvokeAsync(tool, "get_message", $"{{\"messageUrl\":\"{reference}\"}}");

        Assert.Contains("报价 20 元", result);
        Assert.Equal(groupId, messageService.ForwardGroupId);
        Assert.Equal(reference, messageService.ForwardReference);
    }

    [Fact]
    public async Task GetMessage_NonLocalReference_ThrowsForModelError()
    {
        StubMessageService messageService = new();
        MessageTool tool = CreateTool(messageService, 123);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(tool, "get_message", "{\"messageUrl\":\"456\"}"));

        Assert.Contains("merrybot://message/...", exception.Message);
        Assert.Contains("不能使用裸 ID 或外部 URL", exception.Message);
        Assert.Null(messageService.MessageReference);
        Assert.Null(messageService.ForwardReference);
    }

    [Fact]
    public async Task GetMessage_UsesSameMessagePartFormattingAsMessageExtract()
    {
        const long groupId = 123;
        IReadOnlyList<TypedMessage> chain =
        [
            TextData.FromText("正文"),
            AtData.FromAt("789"),
            new FaceData { Id = "14" },
            new ForwardData { Id = LocalMessageReference.Forward("forward-id") },
            ReplyData.FromReply(LocalMessageReference.Message(groupId, 999)),
            new ImageData { File = "image.jpg", Summary = "一张图片" },
        ];
        string expectedContent = AgentMessageExtract.BuildMessage(chain, selfId: 0);
        StubMessageService messageService = new()
        {
            MessageResult = new ProcessedMessage(
                LiteDB.ObjectId.NewObjectId(),
                groupId,
                456,
                789,
                "昵称",
                string.Empty,
                string.Empty,
                chain,
                DateTime.UtcNow,
                false),
        };
        MessageTool tool = CreateTool(messageService, groupId);
        string reference = LocalMessageReference.Message(groupId, 456);

        string result = await InvokeAsync(tool, "get_message", $"{{\"messageUrl\":\"{reference}\"}}");

        Assert.EndsWith($": {expectedContent}", result);
    }

    [Fact]
    public async Task GetMessage_RecalledMessage_MarksWithdrawn()
    {
        const long groupId = 123;
        const long messageId = 456;
        ProcessedMessage expected = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            messageId,
            789,
            "昵称",
            string.Empty,
            string.Empty,
            [TextData.FromText("已撤回的内容")],
            DateTime.UtcNow,
            true);
        StubMessageService messageService = new() { MessageResult = expected };
        MessageTool tool = CreateTool(messageService, groupId);
        string reference = LocalMessageReference.Message(groupId, messageId);

        string result = await InvokeAsync(tool, "get_message", $"{{\"messageUrl\":\"{reference}\"}}");

        Assert.Contains("（已撤回）", result);
        Assert.Contains("已撤回的内容", result);
    }

    [Fact]
    public async Task GetGroupContext_RecalledMessage_MarksWithdrawnInsteadOfSkipping()
    {
        const long groupId = 123;
        ProcessedMessage recalled = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            456,
            789,
            "昵称",
            string.Empty,
            string.Empty,
            [TextData.FromText("撤回的消息")],
            DateTime.UtcNow,
            true);
        StubMessageService messageService = new()
        {
            GroupMessages = [recalled],
            GroupCount = 1,
        };
        MessageTool tool = CreateTool(messageService, groupId);

        string result = await InvokeAsync(tool, "get_group_context", "{\"pageSize\":20}");

        Assert.Contains("（已撤回）", result);
        Assert.Contains("撤回的消息", result);
    }

    [Fact]
    public async Task ReferenceExpand_RecalledMessage_MarksWithdrawn()
    {
        const long groupId = 123;
        ProcessedMessage referenced = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            999,
            789,
            "昵称",
            string.Empty,
            string.Empty,
            [TextData.FromText("被引用的原文")],
            DateTime.UtcNow,
            true);
        StubMessageService messageService = new() { ReplyResult = referenced };
        IReadOnlyList<TypedMessage> chain =
        [
            TextData.FromText("回复 "),
            ReplyData.FromReply(LocalMessageReference.Message(groupId, 999)),
        ];

        string result = await AgentMessageExtract.BuildMessageWithReference(chain, selfId: 0, maxReferenceDepth: 5, messageService, groupId);

        Assert.Contains("（已撤回）", result);
        Assert.Contains("被引用的原文", result);
    }

    [Fact]
    public async Task ReferenceExpand_UsesSameEnvelopeAsMessageTool()
    {
        const long groupId = 123;
        var time = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        ProcessedMessage referenced = new(
            LiteDB.ObjectId.NewObjectId(),
            groupId,
            999,
            789,
            "昵称",
            "群昵称",
            string.Empty,
            [TextData.FromText("正文")],
            time,
            false);
        StubMessageService messageService = new() { ReplyResult = referenced };
        IReadOnlyList<TypedMessage> chain = [ReplyData.FromReply(LocalMessageReference.Message(groupId, 999))];

        string result = await AgentMessageExtract.BuildMessageWithReference(chain, selfId: 0, maxReferenceDepth: 5, messageService, groupId);

        Assert.Contains(MessageUtils.FormatFullMessage(referenced, includeKey: true), result);
    }

    private static MessageTool CreateTool(StubMessageService messageService, long groupId)
        => new(
            messageService,
            new NoopMessageChannel(),
            Browser.Instance,
            new SessionKey("qq", "group", groupId.ToString()),
            new VisionRouter(mainHasVision: false, visionClients: null),
            maxImageBytes: 1024 * 1024);

    private static Task<string> InvokeAsync(MessageTool tool, string name, string arguments)
        => tool.InvokeAsync(CancellationToken.None, new ToolCall("call_1", name, arguments), static _ => { });

    private sealed class StubMessageService : IMessageService
    {
        public ProcessedMessage? MessageResult { get; init; }
        public ProcessedMessage? ReplyResult { get; init; }
        public ProcessedForwardMessage? ForwardResult { get; init; }
        public IReadOnlyList<ProcessedMessage>? GroupMessages { get; init; }
        public int GroupCount { get; init; }
        public long? MessageGroupId { get; private set; }
        public string? MessageReference { get; private set; }
        public long? ForwardGroupId { get; private set; }
        public string? ForwardReference { get; private set; }

        public Task<ProcessedMessage?> GetMessageAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default)
        {
            MessageGroupId = groupId;
            MessageReference = messageIdOrReference;
            return Task.FromResult(MessageResult);
        }

        public Task<ProcessedMessage?> GetReplyAsync(long groupId, string messageIdOrReference, CancellationToken cancellationToken = default)
            => Task.FromResult(ReplyResult);

        public Task<ProcessedMessage?> GetMessageByObjectIdAsync(string objectIdHex, CancellationToken cancellationToken = default)
            => Task.FromResult<ProcessedMessage?>(null);

        public Task<ProcessedForwardMessage?> GetForwardAsync(string forwardIdOrReference, long sourceGroupId, CancellationToken cancellationToken = default)
        {
            ForwardGroupId = sourceGroupId;
            ForwardReference = forwardIdOrReference;
            return Task.FromResult(ForwardResult);
        }

        public Task<LocalMessageResource?> GetResourceAsync(string localUri, CancellationToken cancellationToken = default)
            => Task.FromResult<LocalMessageResource?>(null);

        public Task<IReadOnlyList<ProcessedMessage>> GetGroupMessagesBeforeAsync(long groupId, long? beforeMessageId, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(GroupMessages ?? (IReadOnlyList<ProcessedMessage>)Array.Empty<ProcessedMessage>());

        public Task<IReadOnlyList<ProcessedMessage>> GetGroupMessagesBeforeKeyAsync(long groupId, string? beforeMessageKey, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(GroupMessages ?? (IReadOnlyList<ProcessedMessage>)Array.Empty<ProcessedMessage>());

        public Task<int> GetGroupMessageCountAsync(long groupId, CancellationToken cancellationToken = default)
            => Task.FromResult(GroupCount);

        public Task RecordAiMessageAsync(string sessionKey, string messageType, string content, TokenUsage usage)
            => Task.CompletedTask;
    }

    private sealed class NoopMessageChannel : MessageChannel
    {
        public Task SendMessage(SessionKey session, string message) => Task.CompletedTask;

        public Task SendMessage(SessionKey session, IEnumerable<TypedMessage> messageChain) => Task.CompletedTask;
    }
}
