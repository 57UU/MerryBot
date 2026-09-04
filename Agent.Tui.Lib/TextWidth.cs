using System.Text;

namespace Agent.Tui.Lib;

/// <summary>
/// 终端宽度计算与按列切片。核心规则：
/// - ASCII 宽度 1，CJK 全角宽度 2，emoji 按 2 处理，零宽字符宽度 0。
/// - 所有切片都以"显示的列"为单位，正确处理 ANSI 序列与制表符。
/// 借鉴 pi 的 visibleWidth / sliceByColumn 语义。
/// </summary>
public static class TextWidth
{
    public static int Width(Rune r)
    {
        var value = r.Value;
        if (value < 0x20 || value == 0x7f) return 0;           // 控制字符
        if (value >= 0x200b && value <= 0x200f) return 0;      // 零宽
        if (value == 0xfe0f) return 0;                         // VS16
        if (value >= 0x1f000 && value <= 0x1faff) return 2;    // emoji 区
        if (value >= 0x2300 && value <= 0x27bf) return 2;      // 杂项符号/dingbat
        if (value >= 0x2b00 && value <= 0x2bff) return 2;      // 箭头/星
        if (value >= 0x2e80 && value <= 0x9fff) return 2;      // CJK 部首/汉字
        if (value >= 0xac00 && value <= 0xd7af) return 2;      // 谚文
        if (value >= 0xf900 && value <= 0xfaff) return 2;      // CJK 兼容
        if (value >= 0xfe30 && value <= 0xfe4f) return 2;      // CJK 标点
        if (value >= 0xff00 && value <= 0xff60) return 2;      // 全角 ASCII/标点
        if (value >= 0xffe0 && value <= 0xffe6) return 2;      // 全角符号
        return 1;
    }

    /// <summary>字符串的显示宽度（忽略 ANSI 序列）。</summary>
    public static int Measure(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (!text.Contains('\x1b'))
        {
            return MeasurePlain(text);
        }
        // 含 ANSI：先剥序列再计宽
        var plain = Ansi.StripAnsi(text);
        return MeasurePlain(plain.ToString());
    }

    private static int MeasurePlain(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += Width(rune);
        }
        return width;
    }

    /// <summary>
    /// 把 text 按显示宽度截断到 maxWidth 列，返回截断后的字符串（含原 ANSI 样式）。
    /// 截断点如果落在宽字符中间，丢弃该字符（宽度溢出则少放）。
    /// </summary>
    public static string Truncate(string text, int maxWidth, string ellipsis = "…")
    {
        if (maxWidth <= 0) return string.Empty;
        if (Measure(text) <= maxWidth) return text;

        var plain = Ansi.StripAnsi(text).ToString();
        var sb = new StringBuilder();
        int w = 0;
        foreach (var rune in plain.EnumerateRunes())
        {
            var rw = Width(rune);
            if (w + rw > maxWidth) break;
            sb.Append(rune.ToString());
            w += rw;
            if (w >= maxWidth) break;
        }
        // 留 ellipsis 的宽度
        var eWidth = MeasurePlain(ellipsis);
        while (w + eWidth > maxWidth && sb.Length > 0)
        {
            // 逐个移除字符直到放得下 … 
            var last = sb.ToString();
            var lastRune = last.EnumerateRunes().Last();
            sb.Remove(sb.Length - lastRune.ToString().Length, lastRune.ToString().Length);
            w -= Width(lastRune);
        }
        return sb.ToString() + ellipsis;
    }

    /// <summary>
    /// 左对齐并补空格到指定显示宽度。超过宽度则截断。
    /// </summary>
    public static string PadRight(string text, int width)
    {
        var w = Measure(text);
        if (w >= width) return Truncate(text, width, "");
        return text + new string(' ', width - w);
    }
}