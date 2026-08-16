using System.Text;

namespace Agent.Tui.Core;

/// <summary>按键枚举（归一化，与平台无关）。</summary>
public enum Key
{
    None, Char, Enter, Escape, Tab, BackTab, Backspace, Delete, Insert,
    Home, End, PageUp, PageDown, Up, Down, Left, Right,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

/// <summary>一次按键输入事件的归一化表示。</summary>
public readonly record struct KeyEvent(
    Key Key,
    char Char,
    bool Ctrl,
    bool Alt,
    bool Shift,
    string? Paste = null)
{
    public static KeyEvent Plain(char c) => new(Key.Char, c, false, false, false);
    public static KeyEvent Named(Key key) => new(key, '\0', false, false, false);
}

/// <summary>
/// 键盘解析器：基于 <see cref="Console.ReadKey"/> 归一化按键事件。
/// .NET 在 Unix 上已正确解码方向键/功能键/修饰键转义序列,并自动处理 raw mode,
/// 比自研流读取+超时取消更可靠(后者在 ReadAsync 取消后会导致流状态损坏)。
/// </summary>
public sealed class KeyParser
{
    private readonly Stream _stdin = Console.OpenStandardInput();
    private readonly MemoryStream _paste = new();

    /// <summary>读取并解析一次按键事件（阻塞）。</summary>
    public KeyEvent Read()
    {
        var key = Console.ReadKey(intercept: true);
        // 仅当首键是 Escape 时探测 bracketed paste 开始(ESC[200~)。
        // 普通键零预读,保证键序正确。
        if (key.Key == ConsoleKey.Escape && TryStartPasteAfterEsc())
        {
            return ReadPaste();
        }
        return FromConsoleKey(key);
    }

    /// <summary>
    /// 在已消费 Escape 键后探测:后续字节若拼成 [200~ 则消费整个序列返回 true。
    /// 后续不是 paste 时返回 false,调用方把该 Escape 当作普通 Escape 键返回。
    /// (方向键等功能键由 ReadKey 直接解码为对应 ConsoleKey,不会走到 Escape 分支。)
    /// </summary>
    private bool TryStartPasteAfterEsc()
    {
        // 无待读输入 = 裸 ESC,直接返回 Escape 键
        if (!Console.KeyAvailable) return false;
        // 后续应为 [200~(5 字节: 5b 32 30 30 7e)。轮询拼装,超时视为非 paste。
        var seq = new List<char>(5);
        for (int i = 0; i < 5; i++)
        {
            if (!WaitForInput(100)) return false;
            seq.Add(Console.ReadKey(intercept: true).KeyChar);
        }
        return new string(seq.ToArray()) == "[200~";
    }

    private static bool WaitForInput(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Console.KeyAvailable) return true;
            Thread.Sleep(2);
        }
        return false;
    }

    /// <summary>读取 bracketed paste 内容直到 ESC[201~,返回 Paste 事件。</summary>
    private KeyEvent ReadPaste()
    {
        _paste.SetLength(0);
        var endSeq = new byte[] { 0x1b, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };
        var tail = new byte[6];
        int tailLen = 0;
        while (true)
        {
            var b = _stdin.ReadByte();
            if (b < 0) break;
            _paste.WriteByte((byte)b);
            // 维护最近 6 字节窗口检测结束序列
            tail[tailLen % 6] = (byte)b;
            tailLen++;
            if (tailLen >= 6 && MatchesTail(tail, tailLen, endSeq))
            {
                break;
            }
        }
        var bytes = _paste.ToArray();
        // 移除末尾误入缓冲的结束序列 6 字节
        if (bytes.Length >= 6 && bytes.AsSpan(bytes.Length - 6).SequenceEqual(endSeq))
        {
            bytes = bytes[..^6];
        }
        return new KeyEvent(Key.None, '\0', false, false, false, Paste: System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static bool MatchesTail(byte[] tail, int total, byte[] endSeq)
    {
        // tail 是环形缓冲:最近 6 字节 = tail[(total-6)%6 .. total%6) 顺序
        for (int i = 0; i < 6; i++)
        {
            var idx = (total - 6 + i) % 6;
            if (tail[idx] != endSeq[i]) return false;
        }
        return true;
    }

    private static KeyEvent FromConsoleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return KeyEvent.Named(Key.Enter);
            case ConsoleKey.Escape:
                return KeyEvent.Named(Key.Escape);
            case ConsoleKey.Backspace:
                return KeyEvent.Named(Key.Backspace);
            case ConsoleKey.Delete:
                return KeyEvent.Named(Key.Delete);
            case ConsoleKey.Tab:
                return key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                    ? KeyEvent.Named(Key.BackTab)
                    : KeyEvent.Named(Key.Tab);
            case ConsoleKey.UpArrow:
                return KeyEvent.Named(Key.Up);
            case ConsoleKey.DownArrow:
                return KeyEvent.Named(Key.Down);
            case ConsoleKey.LeftArrow:
                return KeyEvent.Named(Key.Left);
            case ConsoleKey.RightArrow:
                return KeyEvent.Named(Key.Right);
            case ConsoleKey.Home:
                return KeyEvent.Named(Key.Home);
            case ConsoleKey.End:
                return KeyEvent.Named(Key.End);
            case ConsoleKey.PageUp:
                return KeyEvent.Named(Key.PageUp);
            case ConsoleKey.PageDown:
                return KeyEvent.Named(Key.PageDown);
            case ConsoleKey.Insert:
                return KeyEvent.Named(Key.Insert);
            case ConsoleKey.F1: return KeyEvent.Named(Key.F1);
            case ConsoleKey.F2: return KeyEvent.Named(Key.F2);
            case ConsoleKey.F3: return KeyEvent.Named(Key.F3);
            case ConsoleKey.F4: return KeyEvent.Named(Key.F4);
            case ConsoleKey.F5: return KeyEvent.Named(Key.F5);
            case ConsoleKey.F6: return KeyEvent.Named(Key.F6);
            case ConsoleKey.F7: return KeyEvent.Named(Key.F7);
            case ConsoleKey.F8: return KeyEvent.Named(Key.F8);
            case ConsoleKey.F9: return KeyEvent.Named(Key.F9);
            case ConsoleKey.F10: return KeyEvent.Named(Key.F10);
            case ConsoleKey.F11: return KeyEvent.Named(Key.F11);
            case ConsoleKey.F12: return KeyEvent.Named(Key.F12);
            default:
                break;
        }

        // 可打印字符(含中文等多字节)
        if (key.KeyChar >= 0x20 && key.KeyChar != 0x7f)
        {
            return KeyEvent.Plain(key.KeyChar);
        }
        // 控制字符:Ctrl+字母等
        return new KeyEvent(Key.Char, key.KeyChar, key.Modifiers.HasFlag(ConsoleModifiers.Control), key.Modifiers.HasFlag(ConsoleModifiers.Alt), key.Modifiers.HasFlag(ConsoleModifiers.Shift));
    }
}