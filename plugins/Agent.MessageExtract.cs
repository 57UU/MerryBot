using NapcatClient.MessageType;
using System.Text;

namespace BotPlugin;

internal static class AgentMessageExtract
{
    private const int MaxReferenceChars = 6000;

    public static string BuildMessage(IReadOnlyList<TypedMessage> messageChain, long selfId)
        => BuildMessageWithReference(messageChain, selfId, maxReferenceDepth: 0, messageService: null).GetAwaiter().GetResult();

    public static Task<string> BuildMessageWithReference(
        IReadOnlyList<TypedMessage> messageChain,
        long selfId,
        int maxReferenceDepth,
        IMessageService? messageService,
        long groupId = 0)
        => BuildMessageWithDepthAsync(messageChain, selfId, groupId, maxReferenceDepth, messageService, new HashSet<string>());

    private static async Task<string> BuildMessageWithDepthAsync(
        IReadOnlyList<TypedMessage> messageChain,
        long selfId,
        long groupId,
        int remainingDepth,
        IMessageService? messageService,
        HashSet<string> visitedMessages)
    {
        StringBuilder sb = new();
        foreach (TypedMessage message in messageChain)
        {
            string text = message switch
            {
                ReplyData replyData => await ExpandReplyAsync(replyData, selfId, groupId, remainingDepth, messageService, visitedMessages),
                _ => MessageUtils.FormatMessagePart(message),
            };
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static async Task<string> ExpandReplyAsync(
        ReplyData replyData,
        long selfId,
        long groupId,
        int remainingDepth,
        IMessageService? messageService,
        HashSet<string> visitedMessages)
    {
        var placeholder = MessageUtils.FormatMessagePart(replyData);
        if (remainingDepth <= 0 || messageService == null || groupId == 0)
        {
            return placeholder;
        }
        var key = $"{groupId}:{replyData.Id}";
        if (!visitedMessages.Add(key))
        {
            return $"<reference>[引用循环已截断 {replyData.Id}]</reference>";
        }
        try
        {
            ProcessedMessage? referenced;
            try
            {
                referenced = await messageService.GetReplyAsync(groupId, replyData.Id);
            }
            catch (Exception)
            {
                return $"<reference>{placeholder}</reference>";
            }
            if (referenced == null)
            {
                return $"<reference>{placeholder}</reference>";
            }
            var inner = await BuildMessageWithDepthAsync(
                referenced.MessageChain, selfId, groupId, remainingDepth - 1, messageService, visitedMessages);
            inner = CapReference(inner);
            var formatted = MessageUtils.FormatFullMessage(
                referenced with { MessageChain = [TextData.FromText(inner)] }, includeKey: true);
            return $"<reference>{CapReference(formatted)}</reference>";
        }
        finally
        {
            visitedMessages.Remove(key);
        }
    }

    private static string CapReference(string text)
        => text.Length <= MaxReferenceChars ? text : text[..MaxReferenceChars] + $"…（引用内容过长已截断，全文共 {text.Length} 字符）";
}
