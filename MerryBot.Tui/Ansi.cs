using System.Text;

namespace Agent.Tui.Core;

/// <summary>
/// ANSI 转义序列常量与样式包装。最小集合：SGR 颜色 / 差分渲染用的定位与清屏。
/// 借鉴 pi 的自研 ANSI 渲染理念——不做完整终端模拟，只输出终端能理解的转义。
/// </summary>
public static class Ansi
{
    // 光标定位: 1-based row/col
    public const string CursorHome = "\x1b[H";
    public const string ClearScreen = "\x1b[2J";
    public const string HideCursor = "\x1b[?25l";
    public const string ShowCursor = "\x1b[?25h";
    public const string Reset = "\x1b[0m";
    public const string EnterAltScreen = "\x1b[?1049h";
    public const string LeaveAltScreen = "\x1b[?1049l";
    public const string EnableBracketedPaste = "\x1b[?2004h";
    public const string DisableBracketedPaste = "\x1b[?2004l";

    public static string CursorTo(int row, int col) => $"\x1b[{row};{col}H";
    public static string CursorUp(int n) => n > 0 ? $"\x1b[{n}A" : string.Empty;
    public static string ClearLine => "\x1b[2K";
    public static string ClearLineAfter => "\x1b[K";

    /// <summary>SGR 前景色(0-255，含明亮)。</summary>
    public static string Fg(int color) => color < 8 ? $"\x1b[{30 + color}m"
        : color < 16 ? $"\x1b[{90 + color - 8}m"
        : $"\x1b[38;5;{color}m";

    public static string Dim => "\x1b[2m";
    public static string Bold => "\x1b[1m";
    public static string Reverse => "\x1b[7m";
    public static string NoReverse => "\x1b[27m";
    public static string NoDim => "\x1b[22m";

    /// <summary>把一段文本用 SGR 前缀包裹，RESET 收尾。</summary>
    public static string Wrap(string prefix, string text) => prefix + text + Reset;

    /// <summary>
    /// 文本的行内拆分：把 ANSI 序列从可视文本中剥离，返回 (可视文本, 该行累积的样式前缀，
    /// 是否含 RESET)。用于行比较时忽略样式差异、以及拼接时保留样式。
    /// 简化实现：假定样式总是前缀式包裹（渲染层保证），这里只负责剥离。
    /// </summary>
    public static ReadOnlySpan<char> StripAnsi(ReadOnlySpan<char> input)
    {
        // 快路径：无 ESC 直接返回
        if (!input.Contains('\x1b')) return input;
        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\x1b' && i + 1 < input.Length)
            {
                // OSC (ESC ] ... BEL/ST) 或 CSI (ESC [ ... 中间字节)
                if (input[i + 1] == ']')
                {
                    i += 2;
                    while (i < input.Length && input[i] != '\x07' && !(input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '\\'))
                        i++;
                    i++; // 跳过 BEL 或 ESC 的 \\ 部分
                    continue;
                }
                if (input[i + 1] == '[')
                {
                    i += 2;
                    while (i < input.Length && input[i] >= 0x20 && input[i] <= 0x3F) i++; // 参数+中间字节
                    if (i < input.Length && input[i] >= 0x40 && input[i] <= 0x7E) i++;      // 终结字节
                    continue;
                }
                if (input[i + 1] == 'P' || input[i + 1] == 'X' || input[i + 1] == '^' || input[i + 1] == '_')
                {
                    i += 2;
                    while (i < input.Length && input[i] != '\x07'
                        && !(input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '\\'))
                        i++;
                    i++;
                    continue;
                }
                i += 2; // 裸 ESC x：跳过两字符
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}