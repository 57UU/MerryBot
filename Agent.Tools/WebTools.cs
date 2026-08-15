using BrowserService;
using LlmBackend;
using System.ComponentModel;

namespace Agent.Tools;

/// <summary>
/// 网络工具集：注册 web_search（Bing 搜索）与 web_fetch（网页正文读取）两个 LLM 工具。
/// 基于 Browser（无头 Chrome）实现，借助其隐身爬虫能力规避反爬；返回内容为整理后的文本，
/// 输出长度有上限，避免撑爆上下文。
/// </summary>
public class WebTools : ToolSet
{
    /// <summary>搜索结果 / 正文最大输出长度（字符）</summary>
    private const int MaxOutputLength = 6000;

    private readonly Browser browser;
    private readonly ToolSetBridge bridge;

    public WebTools(Browser browser)
    {
        this.browser = browser;
        var builder = new ToolSetBridge.Builder(
            "需要获取实时信息、搜索结果或指定网页内容时，使用 web_search / web_fetch 工具，返回内容已是整理后的文本。");
        builder.AddFunction<WebSearchArgs>("web_search", "搜索网络，返回相关网页的标题、链接与摘要", SearchAsync);
        builder.AddFunction<WebFetchArgs>("web_fetch", "抓取指定 URL 的网页正文文本", FetchAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：web_search</summary>
    private sealed class WebSearchArgs
    {
        [Description("搜索关键词")]
        public string query { get; set; } = string.Empty;
    }

    /// <summary>工具参数：web_fetch</summary>
    private sealed class WebFetchArgs
    {
        [Description("要抓取的网页 URL")]
        public string url { get; set; } = string.Empty;
    }

    private async Task<string> SearchAsync(WebSearchArgs args, CancellationToken cancellationToken, Action<Message> onIterationAdd)
    {
        var query = args.query?.Trim() ?? string.Empty;
        if (query.Length == 0) throw new ArgumentException("query 参数不能为空");
        // Browser.Search 不接受 CancellationToken（Browser API 无此参数），调用链无法继续传递；
        // token 保留在签名中，待 Browser API 支持后直接透传。目标主机固定为 Bing（内部构造 URL），
        // 不涉及用户可控 URL，无需 SSRF 校验。
        _ = cancellationToken;
        return Cap(await browser.Search(query, false));
    }

    private async Task<string> FetchAsync(WebFetchArgs args, CancellationToken cancellationToken, Action<Message> onIterationAdd)
    {
        var url = args.url?.Trim() ?? string.Empty;
        if (url.Length == 0) throw new ArgumentException("url 参数不能为空");
        // 仅允许 http/https scheme，其余一律拒绝
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"URL 无效: {url}");
        }
        // Browser.View 不接受 CancellationToken（Browser API 无此参数），调用链无法继续传递
        var text = await browser.View(url);
        if (text.Length == 0) throw new InvalidOperationException($"页面未提取到文本内容: {url}");
        return Cap(text);
    }

    private static string Cap(string text) =>
        text.Length <= MaxOutputLength
            ? text
            : text[..MaxOutputLength] + $"\n…（内容过长已截断，全文共 {text.Length} 字符）";
}
