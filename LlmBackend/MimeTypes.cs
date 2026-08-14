namespace LlmBackend;

/// <summary>
/// 媒体类型(Content-Type)与 data URL 工具方法,供各 ToolSet 与 VisionRouter 复用,
/// 避免图片类型猜测逻辑在多处重复实现。
/// </summary>
public static class MimeTypes
{
    /// <summary>
    /// 根据文件名、URL 或引用中的扩展名/关键字猜测图片 Content-Type;
    /// 无法识别时返回 null,调用方自行兜底(通常为 image/png)。
    /// </summary>
    public static string? GuessImageContentType(string? pathOrReference)
    {
        if (string.IsNullOrWhiteSpace(pathOrReference)) return null;
        var lower = pathOrReference.ToLowerInvariant();
        if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.Contains("jpeg") || lower.Contains("jpg"))
            return "image/jpeg";
        if (lower.EndsWith(".gif") || lower.Contains("gif"))
            return "image/gif";
        if (lower.EndsWith(".webp") || lower.Contains("webp"))
            return "image/webp";
        if (lower.EndsWith(".png") || lower.Contains("png"))
            return "image/png";
        if (lower.EndsWith(".bmp") || lower.Contains("bmp"))
            return "image/bmp";
        return null;
    }

    /// <summary>
    /// 将图片字节构造为 base64 data URL(供 MessagePartImage.image 使用)。
    /// mimeType 为空时兜底为 image/png。
    /// </summary>
    public static string ToDataUrl(byte[] data, string mimeType)
    {
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
        return $"data:{mime};base64,{Convert.ToBase64String(data)}";
    }
}
