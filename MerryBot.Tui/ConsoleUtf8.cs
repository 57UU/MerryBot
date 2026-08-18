using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Tui.Core;

/// <summary>
/// 传统 Windows 控制台（conhost）显式 UTF-8 模式激活。
///
/// 问题背景：传统 conhost 默认输出/输入代码页是 GBK(936) 等本地代码页，
/// 而 TUI 直接向 stdout 写 UTF-8 字节，会被按本地代码页解码导致中文/符号乱码。
/// 本类在 Windows 上显式执行：
/// 1. 把控制台输出/输入代码页切到 65001(UTF-8)（等价于 chcp 65001），
///    通过设置 <see cref="Console.OutputEncoding"/>/<see cref="Console.InputEncoding"/>
///    完成（setter 内部即调用 SetConsoleOutputCP/SetConsoleCP）；
/// 2. 对 stdout 句柄启用 ENABLE_VIRTUAL_TERMINAL_PROCESSING——传统 conhost
///    默认关闭 VT，ANSI 序列（alt screen/颜色/光标移动）会原样打印，
///    与 UTF-8 乱码叠加导致整屏错乱。
///
/// 退出时调用 <see cref="Disable"/> 恢复原代码页与终端模式（与 RawMode 的
/// 进入/恢复对称）。Unix 终端默认 UTF-8，本类为无操作；
/// stdout/stdin 被重定向时设置代码页失败，静默降级（重定向输出本就是 UTF-8 字节）。
/// </summary>
public static class ConsoleUtf8
{
    private const uint CodePageUtf8 = 65001;
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private static readonly object Sync = new();
    private static bool _active;

    private static Encoding? _oldOutputEncoding;
    private static Encoding? _oldInputEncoding;
    private static IntPtr _outputHandle;
    private static uint _oldOutputMode;
    private static bool _hasOutputMode;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>激活 UTF-8 代码页与 VT 输出；重复调用为幂等。</summary>
    public static void Enable()
    {
        lock (Sync)
        {
            if (_active) return;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _oldOutputEncoding = Console.OutputEncoding;
                    _oldInputEncoding = Console.InputEncoding;
                    // setter 在 Windows 上即 SetConsoleOutputCP/SetConsoleCP(65001)
                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                    // 启用 stdout 的 VT 序列处理（传统 conhost 默认关闭）
                    _outputHandle = GetStdHandle(StdOutputHandle);
                    if (_outputHandle != IntPtr.Zero && _outputHandle != new IntPtr(-1)
                        && GetConsoleMode(_outputHandle, out _oldOutputMode))
                    {
                        SetConsoleMode(_outputHandle, _oldOutputMode | EnableVirtualTerminalProcessing);
                        _hasOutputMode = true;
                    }
                }
                catch
                {
                    // 句柄被重定向/控制台不支持时静默降级，不阻塞 TUI 启动
                }
            }
            _active = true;
        }
    }

    /// <summary>恢复原代码页与终端模式；未启用时为幂等。</summary>
    public static void Disable()
    {
        lock (Sync)
        {
            if (!_active) return;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (_hasOutputMode && _outputHandle != IntPtr.Zero)
                    {
                        SetConsoleMode(_outputHandle, _oldOutputMode);
                    }
                    if (_oldInputEncoding is not null)
                    {
                        Console.InputEncoding = _oldInputEncoding;
                    }
                    if (_oldOutputEncoding is not null)
                    {
                        Console.OutputEncoding = _oldOutputEncoding;
                    }
                }
                catch
                {
                    // 恢复失败不阻塞退出
                }
                finally
                {
                    _hasOutputMode = false;
                    _oldInputEncoding = null;
                    _oldOutputEncoding = null;
                }
            }
            _active = false;
        }
    }
}
