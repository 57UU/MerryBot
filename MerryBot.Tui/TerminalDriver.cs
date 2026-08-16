using System.Text;

namespace Agent.Tui.Core;

/// <summary>
/// 终端驱动：raw mode 下的键盘读取、尺寸、光标可见性与输出。
/// 输出走原始 stdout 流（避免 Console.Out 的 VT/换行处理干扰 ANSI 序列）。
/// </summary>
public sealed class TerminalDriver
{
    private readonly object _writeLock = new();
    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly System.Text.Encoding _enc = new System.Text.UTF8Encoding(false);

    public int Width => Console.WindowWidth;
    public int Height => Console.WindowHeight;

    public void EnterAltScreen()
    {
        Write(Ansi.EnterAltScreen + Ansi.EnableBracketedPaste + Ansi.HideCursor);
        Write("\x1b[2J\x1b[H");
    }

    public void LeaveAltScreen()
    {
        Write(Ansi.ShowCursor + Ansi.DisableBracketedPaste + Ansi.LeaveAltScreen);
    }

    public void Write(string s)
    {
        lock (_writeLock)
        {
            var bytes = _enc.GetBytes(s);
            _stdout.Write(bytes, 0, bytes.Length);
            _stdout.Flush();
        }
    }

    public void ShowCursor() => Write(Ansi.ShowCursor);
    public void HideCursor() => Write(Ansi.HideCursor);

    /// <summary>定位硬件光标到 (row, col)（1-based）。</summary>
    public void PositionCursor(int row, int col) => Write(Ansi.CursorTo(row, col));

    public void SetTitle(string title)
    {
        Write($"\x1b]0;{title}\x07");
    }

    /// <summary>
    /// 阻塞读取一次按键输入。raw mode 下方向键/功能键由 Console.ReadKey 解码为 ConsoleKeyInfo，
    /// 同时保留原始转义串（用于粘贴模式等需要精确判断的场景）。
    /// </summary>
    public ConsoleKeyInfo ReadKey()
    {
        return Console.ReadKey(intercept: true);
    }
}