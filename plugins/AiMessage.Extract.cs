using CommonLib;
using NapcatClient;
using NapcatClient.MessageType;
using System.Text;
using System.Text.Json;
using ZhipuClient;

namespace BotPlugin;

public partial class AiMessage
{
    internal async Task<string> ExtractMessage(IEnumerable<TypedMessage> chain, long groupId, bool recursive = false, int depth = 0, ResourceLimit? resourceLimit = null)
    {
        var limit = resourceLimit ?? new ResourceLimit();
        var items = chain as IList<TypedMessage> ?? chain.ToList();
        StringBuilder sb = new();
        foreach (var item in items)
        {
            var result = await ProcessMessageItem(item, groupId, recursive, depth, limit);
            if (!string.IsNullOrEmpty(result))
            {
                sb.Append(result);
            }
        }

        return sb.ToString();
    }

    async Task<string> ProcessMessageItem(TypedMessage item, long groupId, bool recursive, int depth, ResourceLimit limit)
    {
        if (item is TextData textData)
        {
            return AppendTextData(textData);
        }
        else if (item is AtData atData)
        {
            return await AppendAtData(atData, groupId);
        }
        else if (item is ReplyData replyData && recursive)
        {
            return await AppendReplyData(replyData, groupId, depth, limit);
        }
        else if (item is JsonData jsonData)
        {
            return AppendJsonData(jsonData);
        }
        else if (item is ForwardData forwardData)
        {
            return await AppendForwardData(forwardData, groupId, depth);
        }
        else if (item is ImageData imageData)
        {
            return await AppendImageData(imageData, depth, limit);
        }
        else if (item is FaceData faceData)
        {
            return AppendFaceData(faceData);
        }
        else if (item is FileData fileData)
        {
            return GetFileData(fileData);
        }

        return "";
    }
    string GetFileData(FileData fileData)
    {
        return $"<file name={fileData.File} size={Format.FormatFileSize(fileData.FileSize ?? 0)}/>";
    }

    string AppendTextData(TextData textData)
    {
        return textData.Text.Trim();
    }

    async Task<string> AppendAtData(AtData atData, long groupId)
    {
        string qq = atData.Qq;
        var detail = await Actions.GetGroupMemberData(groupId.ToString(), qq);
        if (detail != null)
        {
            return $" @{detail.Nickname} ";
        }
        else
        {
            return $" @unknown ";
        }
    }

    async Task<string> AppendReplyData(ReplyData replyData, long groupId, int depth, ResourceLimit resourceLimit)
    {
        string? referenceMessage = null;
        string referMessageId = replyData.Id;
        var referMessage = await Actions.GetMessageById(referMessageId);
        if (referMessage != null)
        {
            var extractedMessage = await ExtractMessage(referMessage.Message, groupId, false, depth + 1, resourceLimit: resourceLimit);
            referenceMessage = $"<引用内容：{extractedMessage}/>";
        }
        return referenceMessage ?? "";
    }

    string AppendJsonData(JsonData jsonData)
    {
        JsonElement json = jsonData.Data;
        if (json.ValueKind == JsonValueKind.Undefined || json.ValueKind == JsonValueKind.Null)
        {
            return "";
        }
        if (json.ValueKind == JsonValueKind.String)
        {
            var rawJson = json.GetString();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return "";
            }
            try
            {
                using var parsedJson = JsonDocument.Parse(rawJson);
                json = parsedJson.RootElement.Clone();
            }
            catch (JsonException)
            {
                return rawJson;
            }
        }
        if (json.ValueKind != JsonValueKind.Object)
        {
            return json.ToString();
        }
        if (json.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("news", out var news) && news.ValueKind == JsonValueKind.Object)
            {
                if (news.TryGetProperty("desc", out var desc) && news.TryGetProperty("jumpUrl", out var jumpUrl))
                {
                    return
                        $"描述:{desc}\n" +
                        $"URL:'{jumpUrl}\n'";
                }
                return news.ToString();
            }
            return meta.ToString();
        }
        return json.ToString();
    }

    async Task<string> AppendForwardData(ForwardData forwardData, long groupId, int depth)
    {
        string msgId = forwardData.Id;
        var referMessage = await Actions.GetForwardMessageById(msgId);
        if (referMessage != null)
        {
            List<string> forwardLines = new();
            forwardLines.Add("<转发消息>");
            foreach (var msg in referMessage.Messages)
            {
                var extractedMessage = await ExtractMessage(msg.Message, groupId, depth < 3, depth + 1);
                forwardLines.Add($"{msg.SenderInfo.nickname}:{extractedMessage}");
            }
            forwardLines.Add("</转发消息>");
            var forwardString = string.Join(Environment.NewLine, forwardLines);
            return PluginUtils.ConstraintLength(forwardString, 600);
        }
        else
        {
            return "<转发消息/>";
        }
    }

    async Task<string> AppendImageData(ImageData imageData, int depth, ResourceLimit limit)
    {
        if (ImageInterpreterPool != null && imageData.Url != null && limit.CanUseImageInterpreter(depth))
        {
            await limit.ImageInterpreterSemaphore.WaitAsync();
            var imageUrl = imageData.Url;
            byte[]? imageBytes = null;
            if (!imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(imageUrl, out var imageId))
                {
                    var imageEntry = await storageManager.GroupHistoryRecorder.GetImageByIdAsync(imageId);
                    if (imageEntry != null)
                    {
                        imageBytes = await storageManager.GroupHistoryRecorder.GetImageDataAsync(imageEntry.Hash);
                    }
                }
            }
            try
            {
                var description = imageBytes != null
                    ? await ImageInterpreterPool!.Interpret(imageBytes, limit.ImageInterpreterType)
                    : await ImageInterpreterPool!.Interpret(imageUrl, limit.ImageInterpreterType);
                if (!limit.TryConsumeImageInterpreter(depth))
                {
                    return $"<image {imageData.Summary}/>";
                }
                return $"<image：{description}/>";
            }
            catch (Exception)
            {
                return $"<image {imageData.Summary}/>";
            }
            finally
            {
                limit.ImageInterpreterSemaphore.Release();
            }
        }
        else
        {
            return $"<image {imageData.Summary}/>";
        }
    }

    static string GetImageContentType(string url)
    {
        var extension = Path.GetExtension(url).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    string AppendFaceData(FaceData faceData)
    {
        if (int.TryParse(faceData.Id, out int faceCode))
        {
            string faceName = QqFace.GetFace(faceCode);
            return $" <表情:{faceName}/>";
        }
        return "<表情unknown/>";
    }
}


internal class ResourceLimit
{
    readonly object locker = new();
    public SemaphoreSlim ImageInterpreterSemaphore { get; } = new(5);
    int imageLimit = 3;
    public int ImageLimit
    {
        get
        {
            lock (locker)
            {
                return imageLimit;
            }
        }
        set
        {
            lock (locker)
            {
                imageLimit = value;
            }
        }
    }
    public bool CanUseImageInterpreter(int depth)
    {
        lock (locker)
        {
            return depth == 0 || imageLimit > 0;
        }
    }
    public bool TryConsumeImageInterpreter(int depth)
    {
        lock (locker)
        {
            if (depth != 0 && imageLimit <= 0)
            {
                return false;
            }
            imageLimit--;
            return true;
        }
    }
    public ImageInterpreterType ImageInterpreterType { get; set; } = ImageInterpreterType.Normal;
}
