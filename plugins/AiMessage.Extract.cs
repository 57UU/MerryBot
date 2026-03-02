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
        string? referenceMessage = null;
        foreach (var item in chain)
        {
            if (item is TextData textData)
            {
                sb.Append(textData.Text.Trim());
            }
            else if (item is AtData atData)
            {
                string qq = atData.Qq;
                var detail = await Actions.GetGroupMemberData(groupId.ToString(), qq);
                if (detail != null)
                {
                    sb.Append($" @{detail.Nickname} ");
                }
                else
                {
                    sb.Append($" @unknown ");
                }

            }
            else if (item is ReplyData replyData && recursive)
            {
                string referMessageId = replyData.Id;
                var referMessage = await Actions.GetMessageById(referMessageId);
                if (referMessage != null)
                {
                    var extractedMessage = await ExtractMessage(referMessage.Message, groupId, false, depth + 1, interpretImage: true);
                    referenceMessage = $"\n引用内容：\n{extractedMessage}";
                }
            }
            else if (item is JsonData jsonData)
            {
                JsonElement json = jsonData.Data;
                if (json.TryGetProperty("meta", out var meta))
                {
                    if (meta.TryGetProperty("news", out var news))
                    {
                        try
                        {
                            sb.AppendLine(
                                $"描述:{news.GetProperty("desc").ToString()}\n" +
                                $"URL:'{news.GetProperty("jumpUrl").ToString()}'"
                                );
                        }
                        catch (Exception)
                        {
                            sb.AppendLine(news.ToString());
                        }
                    }
                    else
                    {
                        sb.AppendLine(meta.ToString());
                    }
                }
                else
                {
                    sb.AppendLine(json.ToString());
                }
            }
            else if (item is ForwardData forwardData)
            {
                //转发消息
                string msgId = forwardData.Id;
                var referMessage = await Actions.GetForwardMessageById(msgId);
                if (referMessage != null)
                {
                    StringBuilder forwardString = new();
                    forwardString.AppendLine("---转发消息---");
                    foreach (var msg in referMessage.Messages)
                    {
                        var extractedMessage = await ExtractMessage(msg.Message, groupId, depth < 3, depth + 1);
                        forwardString.AppendLine($"{msg.SenderInfo.nickname}:{extractedMessage}");
                    }
                    forwardString.AppendLine("------");
                    sb.AppendLine(
                        PluginUtils.ConstraintLength(forwardString.ToString(), 600)
                        );
                }
                else
                {
                    sb.AppendLine("<转发消息/>");
                }


            }
            else if (item is ImageData imageData)
            {
                if ((depth == 0 || interpretImage) && ImageInterpreterPool != null && imageData.Url != null)
                {
                    //解析图片内容
                    var imageUrl = imageData.Url;
                    try
                    {
                        var description = await ImageInterpreterPool!.Interpret(imageUrl);
                        sb.AppendLine($"<image：{description}/>");
                    }
                    catch (Exception)
                    {
                        sb.AppendLine($"<image/>");
                    }
                }
                else
                {
                    sb.AppendLine("<image/>");
                }

            }
            else if (item is FaceData faceData)
            {
                if (int.TryParse(faceData.Id, out int faceCode))
                {
                    string faceName = QqFace.GetFace(faceCode);
                    sb.AppendLine($" <表情:{faceName}/>");
                }
            }
        }
        if (referenceMessage != null)
        {
            sb.AppendLine(referenceMessage);
        }
        var text = sb.ToString();
        return text;
    }
}
