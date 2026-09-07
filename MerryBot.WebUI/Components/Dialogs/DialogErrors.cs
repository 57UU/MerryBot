namespace MerryBot.WebUI.Components.Dialogs;

/// <summary>对话框与页面共用的服务端错误文案压缩：服务端 500 会把整页 HTML 吐进异常消息，显示前先压成一句话。</summary>
internal static class DialogErrors
{
    internal static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        string text = message.Trim();
        // HTML 错误页可能藏在业务前缀之后（如“保存 Provider 失败：<html>…”），从首次出现处截断
        int htmlIndex = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        int doctypeIndex = text.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
        int cut = htmlIndex >= 0 ? htmlIndex : doctypeIndex;
        if (cut >= 0)
        {
            string reason = text[..cut].TrimEnd(' ', ':', '：');
            return string.IsNullOrEmpty(reason)
                ? "服务器内部错误（返回了 HTML 错误页），请查看服务端日志。"
                : reason + "（服务器返回了 HTML 错误页，详见服务端日志）。";
        }
        const int maxLength = 300;
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
