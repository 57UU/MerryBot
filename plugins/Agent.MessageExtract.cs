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
        foreach (var message in messageChain)
        {
            string text;
            switch (message)
            {
                case TextData textData:
                    text = textData.Text;
                    break;
                case AtData atData when atData.Qq == selfId.ToString():
                    text = string.Empty;
                    break;
                case AtData:
                    text = string.Empty;
                    break;
                case ReplyData replyData:
                    text = await ExpandReplyAsync(replyData, selfId, groupId, remainingDepth, messageService, visitedMessages);
                    break;
                case ForwardData forwardData:
                    text = $"[转发消息 {forwardData.Id}]";
                    break;
                case FaceData faceData:
                    text = $"[表情: {faceData.ToChinese()}]";
                    break;
                case MfaceData mfaceData:
                    text = $"[商城表情: {mfaceData.Summary ?? mfaceData.EmojiId}]";
                    break;
                case DiceData diceData:
                    text = $"[骰子: {diceData.Result}点]";
                    break;
                case RpsData rpsData:
                    text = $"[猜拳: {rpsData.Result switch { "1" => "石头", "2" => "剪刀", _ => "布" }}]";
                    break;
                case PokeData:
                    text = "[戳一戳]";
                    break;
                case ImageData imageData:
                    text = $"[图片: {imageData.Summary ?? imageData.File}]";
                    break;
                case RecordData:
                    text = "[语音]";
                    break;
                case VideoData videoData:
                    text = $"[视频: {videoData.File}]";
                    break;
                case FileData fileData:
                    text = $"[文件: {fileData.File}]";
                    break;
                case JsonData jsonData:
                    text = $"[卡片消息: {jsonData.Data}]";
                    break;
                case MusicData musicData:
                    text = $"[音乐: {musicData.Title ?? musicData.Id ?? musicData.Url}]";
                    break;
                default:
                    text = message.ToString() ?? string.Empty;
                    break;
            }
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
        var placeholder = $"[引用消息 {replyData.Id}]";
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
            var timeStr = referenced.Time.ToString("yyyy-MM-dd HH:mm");
            var name = string.IsNullOrEmpty(referenced.SenderGroupNickname) ? referenced.SenderNickname : referenced.SenderGroupNickname;
            var formatted = $"[{timeStr}] [用户 {referenced.SenderId}(昵称:{name})]: {inner}";
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
