using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrowserService;

/// <summary>
/// Browser 的纯辅助函数：字符串整理、搜索结果格式化、URI 标准化、去重正则。
/// 与基础设施（Browser.cs）、公开操作（Browser.Actions.cs）同为 partial 拆分。
/// </summary>
public partial class Browser
{
    static string Trim(string s)
    {
        s = s.Replace("\n", "").Replace("\r", "");
        return DuplicatedRegex().Replace(s, " ");
    }
    private static string FormatSearchResult(string raw)
    {
        List<SearchResult> obj = JsonSerializer.Deserialize<List<SearchResult>>(raw)!;
        StringBuilder sb = new();
        foreach (SearchResult item in obj)
        {
            sb.AppendLine($"# {item.Title}");
            sb.AppendLine($"- {item.Content}");
            sb.AppendLine($"- {item.Link}");
        }
        return sb.ToString();

    }
    public static Uri ToStandardUri(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("输入不能为空");

        raw = raw.Trim();

        // 1. 如果已经包含 scheme 就原样解析
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(raw, UriKind.Absolute);
        }

        // 2. 否则补 https:// 再解析
        return new Uri("http://" + raw, UriKind.Absolute);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex DuplicatedRegex();
}
