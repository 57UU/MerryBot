using System.Text.RegularExpressions;

namespace LlmClient;

/// <summary>
/// 正文工具调用标记的结构化检测器：识别模型误把工具调用以文本形式输出的情况
/// （DSML 特殊 token、XML 工具调用标签、JSON 工具调用结构），而非使用原生
/// tool_calls 通道。匹配的是语法结构而非固定关键词。
///
/// 检测范围为内容的开头与结尾各 <see cref="WindowSize"/> 字符：开头是标记的
/// 典型位置；部分模型会先输出正常文本、在结尾才吐出错误的工具调用标记。
/// 结尾模式要求标记结构一直延伸到内容末尾（DSML token 起至结束、XML 以闭合
/// 标签收尾、JSON 对象在末尾闭合）——正文中间举例、随后回归正常文本的标记
/// 提及不误判（避免误伤讲解标记语法的合法内容）。
/// </summary>
internal static class StrayToolCallDetector
{
    /// <summary>开头/结尾检测窗口长度</summary>
    internal const int WindowSize = 512;

    private const RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ---------- 开头模式（锚定内容起始，标记的典型位置） ----------

    /// <summary>DSML 等特殊 token 结构：&lt;|DSML|、&lt;|python_tag|&gt; 等</summary>
    private static readonly Regex PrefixSpecialToken = new(
        @"^\s*<\|[A-Za-z_]+\|?", Options);

    /// <summary>XML 工具调用标签结构：&lt;invoke name=...&gt;、&lt;tool_call&gt;、&lt;antml:invoke&gt; 等（含命名空间变体）</summary>
    private static readonly Regex PrefixXmlToolTag = new(
        @"^\s*</?([A-Za-z][A-Za-z0-9]*:)?(tool_calls?|function_calls?|invoke|parameter)\b", Options);

    /// <summary>JSON 工具调用结构：{"name": "...", "arguments": ...} 等</summary>
    private static readonly Regex PrefixJsonToolCall = new(
        @"^\s*\{\s*""(name|function|tool_call|arguments|parameters)""\s*:", Options);

    // ---------- 结尾模式（锚定内容末尾：标记结构延伸到结束才算泄漏） ----------

    /// <summary>特殊 token 起至内容末尾（&lt;|DSML|tool_calls&gt;... 块；该 token 形态在合法正文中几乎不出现）</summary>
    private static readonly Regex TailSpecialTokenBlock = new(
        @"<\|[A-Za-z_]+\|[\s\S]*$", Options);

    /// <summary>XML 工具调用块：含工具标签、以闭合工具标签收尾（如 &lt;invoke name=...&gt;...&lt;/invoke&gt;）</summary>
    private static readonly Regex TailXmlBlock = new(
        @"</?([A-Za-z][A-Za-z0-9]*:)?(tool_calls?|function_calls?|invoke|parameter)\b[\s\S]*</([A-Za-z][A-Za-z0-9]*:)?(tool_calls?|function_calls?|invoke|parameter)\s*>\s*$",
        Options);

    /// <summary>JSON 工具调用块：name 与 arguments/parameters 键成对、对象在内容末尾闭合</summary>
    private static readonly Regex TailJsonBlock = new(
        @"\{\s*""(name|function|tool_call)""\s*:\s*""[^""]*""\s*,\s*""(arguments|parameters)""\s*:[\s\S]*\}\s*$",
        Options);

    /// <summary>
    /// 对完整正文做一次性检测。content 为空时返回 false。
    /// 开头或结尾窗口命中工具调用标记结构即为 true。
    /// </summary>
    public static bool IsStrayToolCallMarkup(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }
        return MatchesPrefix(content) || MatchesTail(content);
    }

    private static bool MatchesPrefix(string content)
    {
        var prefix = content.Length <= WindowSize ? content : content[..WindowSize];
        return PrefixSpecialToken.IsMatch(prefix)
            || PrefixXmlToolTag.IsMatch(prefix)
            || PrefixJsonToolCall.IsMatch(prefix);
    }

    private static bool MatchesTail(string content)
    {
        var tail = content.Length <= WindowSize ? content : content[^WindowSize..];
        return TailSpecialTokenBlock.IsMatch(tail)
            || TailXmlBlock.IsMatch(tail)
            || TailJsonBlock.IsMatch(tail);
    }
}
