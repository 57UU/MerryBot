using System.Text;

namespace Agent.Tui.Lib;

/// <summary>
/// 单行输入框（借鉴 pi 的 Input 组件，裁剪为 C# 版）：
/// - 支持光标左右/行首/行尾、退格、删除、Ctrl+U/Ctrl+W、Esc 取消、Enter 提交
/// - 文本超宽时横向滚动，光标保持在可视区内
/// - 可选前缀（提示标签，如 "❯ " 或提问文本）
/// - 输出 CURSOR_MARKER 让外层定位硬件光标
/// </summary>
public sealed class Input : ComponentBase, IFocusable
{
    internal const string CursorMarker = "\x1b_pi:c\x07"; // 零宽 APC 序列，渲染定位用

    private string _value = string.Empty;
    private int _cursor; // 在 value 中的字符索引
    private bool _focused;
    private int _scrollCol; // 可视区起始显示列

    public string Prefix { get; set; } = "❯ ";
    public Func<string>? PrefixProvider { get; set; }   // 动态前缀（提示模式时切换问题文本）
    public Action<string>? OnSubmit { get; set; }
    public Action? OnEscape { get; set; }

    /// <summary>约等于浏览器/终端输入行的当前值。</summary>
    public string Value
    {
        get => _value;
        set { _value = value ?? string.Empty; _cursor = Math.Min(_cursor, _value.Length); }
    }

    public bool IsFocused
    {
        get => _focused;
        set { _focused = value; if (value) _scrollCol = Math.Max(0, _scrollCol); }
    }

    public override void Invalidate() { }

    public override bool HandleInput(KeyEvent ev)
    {
        // bracketed paste 内容
        if (ev.Paste is { } paste)
        {
            InsertPaste(paste);
            return true;
        }

        switch (ev.Key)
        {
            case Key.Enter:
                OnSubmit?.Invoke(_value);
                return true;
            case Key.Escape:
                OnEscape?.Invoke();
                return true;
            case Key.Char when ev.Ctrl:
                // Ctrl+U / Ctrl+W / Ctrl+C
                switch (ev.Char)
                {
                    case 'u' or '\x15':
                        _value = _value[_cursor..];
                        _cursor = 0;
                        return true;
                    case 'w' or '\x17':
                        var before = _value[.._cursor];
                        var cut = before.LastIndexOf(' ');
                        var newBefore = cut > 0 ? before[..cut] : string.Empty;
                        _value = newBefore + _value[_cursor..];
                        _cursor = newBefore.Length;
                        return true;
                    case 'c' or '\x03':
                        return false; // 交给上层处理（取消/退出）
                    default:
                        return false;
                }
            case Key.Backspace:
                if (_cursor > 0)
                {
                    _value = _value[..(_cursor - 1)] + _value[_cursor..];
                    _cursor--;
                }
                return true;
            case Key.Delete:
                if (_cursor < _value.Length)
                {
                    _value = _value[.._cursor] + _value[(_cursor + 1)..];
                }
                return true;
            case Key.Left:
                if (_cursor > 0) _cursor--;
                return true;
            case Key.Right:
                if (_cursor < _value.Length) _cursor++;
                return true;
            case Key.Home:
                _cursor = 0;
                return true;
            case Key.End:
                _cursor = _value.Length;
                return true;
            case Key.Char:
                // 可打印字符（含中文等多字节）
                if (ev.Char >= 0x20 && ev.Char != 0x7f)
                {
                    var ch = ev.Char.ToString();
                    _value = _value[.._cursor] + ch + _value[_cursor..];
                    _cursor += ch.Length;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>粘贴文本（来自 bracketed paste）：清掉换行/制表符后在光标处插入。</summary>
    public void InsertPaste(string text)
    {
        // 安全:粘贴内容属外部输入,剥离 ESC 防终端注入
        var clean = Ansi.StripAnsi(text ?? string.Empty).ToString()
            .Replace("\r\n", "").Replace("\r", "").Replace("\n", "").Replace("\t", "    ");
        _value = _value[.._cursor] + clean + _value[_cursor..];
        _cursor += clean.Length;
    }

    public override string[] Render(int width)
    {
        var prefix = PrefixProvider?.Invoke() ?? Prefix;
        var prefixWidth = TextWidth.Measure(prefix);
        var avail = Math.Max(0, width - prefixWidth);
        if (avail <= 0)
        {
            return [prefix];
        }

        var cursorCol = TextWidth.Measure(_value[.._cursor]);
        var totalWidth = TextWidth.Measure(_value);

        // 横向滚动：光标保持可见
        if (cursorCol < _scrollCol)
        {
            _scrollCol = cursorCol;
        }
        else if (cursorCol >= _scrollCol + avail)
        {
            _scrollCol = cursorCol - avail + 1;
        }
        _scrollCol = Math.Max(0, _scrollCol);

        var visible = SliceFromColumn(_value, _scrollCol, avail);
        var vc = Math.Max(0, cursorCol - _scrollCol);
        var cursorChar = GetCharAtDisplayCol(visible, vc);
        var before = SliceFromColumn(visible, 0, vc);
        var afterCursor = SliceFromColumn(visible, vc + TextWidth.Measure(cursorChar), avail);
        var pad = new string(' ', Math.Max(0, avail - TextWidth.Measure(visible)));

        string line;
        if (_focused)
        {
            // 光标字符反显，前插 CursorMarker 供外层定位硬件光标
            line = prefix + before + CursorMarker + Ansi.Reverse + cursorChar + Ansi.NoReverse + afterCursor + pad;
        }
        else
        {
            line = prefix + visible + pad;
        }
        return [line];
    }

    private static string GetCharAtDisplayCol(string text, int col)
    {
        var slice = SliceFromColumn(text, col, 1);
        return slice.Length > 0 ? slice : " ";
    }

    /// <summary>从 startCol 显示列开始截取 maxCols 列宽的可视文本。</summary>
    private static string SliceFromColumn(string text, int startCol, int maxCols)
    {
        if (maxCols <= 0) return string.Empty;
        if (startCol <= 0 && TextWidth.Measure(text) <= maxCols) return text;
        var sb = new StringBuilder();
        int col = 0, end = startCol + maxCols;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = TextWidth.Width(rune);
            var runeStart = col;
            col += w;
            if (col <= startCol) continue; // 全在窗口之前
            if (runeStart >= end) break;  // 全在窗口之后
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}