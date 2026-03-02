using CommonLib;
using NapcatClient;
using NapcatClient.MessageType;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace BotPlugin;

public partial class AiMessage
{
    async Task<string> ExtractMessage(IEnumerable<TypedMessage> chain, long groupId, bool recursive = false, int depth = 0, bool interpretImage = false)
    {
        StringBuilder sb = new();
        foreach (var item in chain)
        {
            var result = await ProcessMessageItem(item, groupId, recursive, depth, interpretImage);
            if (!string.IsNullOrEmpty(result))
            {
                sb.Append(result);
            }
        }

        return sb.ToString();
    }

    async Task<string> ProcessMessageItem(TypedMessage item, long groupId, bool recursive, int depth, bool interpretImage)
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
            return await AppendReplyData(replyData, groupId, depth, interpretImage);
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
            return await AppendImageData(imageData, depth, interpretImage);
        }
        else if (item is FaceData faceData)
        {
            return AppendFaceData(faceData);
        }else if(item is FileData fileData)
        {
            return GetFileData(fileData);
        }
        
        return "";
    }
    string GetFileData(FileData fileData) {
        return $"<file name={fileData.File} size={Format.FormatFileSize(fileData.FileSize??0)}/>";
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

    async Task<string> AppendReplyData(ReplyData replyData, long groupId, int depth, bool interpretImage)
    {
        string? referenceMessage = null;
        string referMessageId = replyData.Id;
        var referMessage = await Actions.GetMessageById(referMessageId);
        if (referMessage != null)
        {
            var extractedMessage = await ExtractMessage(referMessage.Message, groupId, false, depth + 1, interpretImage: true);
            referenceMessage = $"<引用内容：{extractedMessage}/>";
        }
        return referenceMessage ?? "";
    }

    string AppendJsonData(JsonData jsonData)
    {
        JsonElement json = jsonData.Data;
        if (json.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("news", out var news))
            {
                try
                {
                    return
                        $"描述:{news.GetProperty("desc").ToString()}\n" +
                        $"URL:'{news.GetProperty("jumpUrl").ToString()}\n'" ;
                }
                catch (Exception)
                {
                    return news.ToString() ;
                }
            }
            else
            {
                return meta.ToString() ;
            }
        }
        else
        {
            return json.ToString() ;
        }
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
            return PluginUtils.ConstraintLength(forwardString, 600) ;
        }
        else
        {
            return "<转发消息/>" ;
        }
    }

    async Task<string> AppendImageData(ImageData imageData, int depth, bool interpretImage)
    {
        if ((depth == 0 || interpretImage) && ImageInterpreterPool != null && imageData.Url != null)
        {
            var imageUrl = imageData.Url;
            try
            {
                var description = await ImageInterpreterPool!.Interpret(imageUrl);
                return $"<image：{description}/>" ;
            }
            catch (Exception)
            {
                return $"<image/>" ;
            }
        }
        else
        {
            return "<image/>" ;
        }
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
