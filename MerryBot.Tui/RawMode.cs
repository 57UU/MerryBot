using System.Runtime.InteropServices;

namespace Agent.Tui.Core;

/// <summary>
/// 终端原始模式控制。目标：关闭行缓冲/回显/信号键，让程序逐键读取输入；
/// 退出时恢复原状态。Linux/macOS 用 termios；Windows 用 SetConsoleMode。
/// </summary>
public static class RawMode
{
    private static bool _active;
    private static readonly object Sync = new();

    // ---- termios (Linux/macOS) ----
    private const int TCSANOW = 0;
    private const int ICANON = 0x0002;
    private const int ECHO = 0x0008;
    private const int ISIG = 0x0001;
    private const int IEXTEN = 0x8000;
    private const int BRKINT = 0x0002;
    private const int ICRNL = 0x0100;
    private const int INPCK = 0x0010;
    private const int ISTRIP = 0x0020;
    private const int IXON = 0x0400;
    private const int OPOST = 0x0001;
    private const int VMIN = 6;
    private const int VTIME = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public uint c_iflag;
        public uint c_oflag;
        public uint c_cflag;
        public uint c_lflag;
        public byte c_line;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] c_cc;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out Termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, ref Termios termios);

    // ---- Windows console mode ----
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>进入原始模式；重复调用为幂等。</summary>
    public static void Enable()
    {
        lock (Sync)
        {
            if (_active) return;
            if (OperatingSystem.IsWindows())
            {
                EnableWindows();
            }
            else
            {
                EnableUnix();
            }
            _active = true;
        }
    }

    /// <summary>恢复终端状态；未启用时为幂等。</summary>
    public static void Disable()
    {
        lock (Sync)
        {
            if (!_active) return;
            if (OperatingSystem.IsWindows())
            {
                DisableWindows();
            }
            else
            {
                DisableUnix();
            }
            _active = false;
        }
    }

    private static uint _winOriginalMode;

    private static void EnableWindows()
    {
        var handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetConsoleMode(handle, out _winOriginalMode)) return;
        // 关闭行缓冲/回显/processed(让 Ctrl+C 作为输入键到达)。
        // 不保留 ENABLE_VIRTUAL_TERMINAL_INPUT:该标志面向"裸字节读 VT 序列",
        // 与 Console.ReadKey 配合反而可能干扰按键枚举识别(回车/退格被错判为控制字符);
        // 输出侧的 VT 由终端/输出句柄处理,与此处输入句柄无关。
        var mode = _winOriginalMode;
        mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT | ENABLE_VIRTUAL_TERMINAL_INPUT);
        SetConsoleMode(handle, mode);
    }

    private static void DisableWindows()
    {
        var handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        SetConsoleMode(handle, _winOriginalMode);
    }

    private static Termios _unixOriginal;
    private static bool _unixRawOk;

    private static void EnableUnix()
    {
        if (tcgetattr(0, out _unixOriginal) != 0)
        {
            Console.Error.WriteLine("[tui] tcgetattr 失败，无法进入 raw mode");
            return;
        }
        // 手动设置 raw 模式（cfmakeraw 是 glibc 宏，2.34+ 不再导出为符号，不能 DllImport）：
        // - 关闭 ICANON（行缓冲）、ECHO（回显）、ISIG（信号键）、IEXTEN（扩展处理）
        // - 关闭输入转换（ICRNL/INPCK/ISTRIP/IXON/BRKINT）与输出转换（OPOST）
        // - VMIN=1 至少读 1 字节返回，VTIME=0 无超时
        var raw = _unixOriginal;
        raw.c_iflag &= unchecked((uint)~(BRKINT | ICRNL | INPCK | ISTRIP | IXON));
        raw.c_oflag &= unchecked((uint)~OPOST);
        raw.c_lflag &= unchecked((uint)~(ECHO | ICANON | IEXTEN | ISIG));
        raw.c_cc[VMIN] = 1;
        raw.c_cc[VTIME] = 0;
        if (tcsetattr(0, TCSANOW, ref raw) != 0)
        {
            Console.Error.WriteLine("[tui] tcsetattr 失败，无法进入 raw mode");
            return;
        }
        _unixRawOk = true;
    }

    private static void DisableUnix()
    {
        if (_unixRawOk)
        {
            tcsetattr(0, TCSANOW, ref _unixOriginal);
            _unixRawOk = false;
        }
    }
}