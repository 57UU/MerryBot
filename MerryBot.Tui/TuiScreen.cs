using System.Text;

namespace Agent.Tui.Core;

/// <summary>
/// 差分渲染引擎（借鉴 pi：每帧渲染组件树 → 逐行比较 → 只重绘变化的行）。
///
/// 设计：
/// - <see cref="LayoutRoot"/> 是一个返回行数组的委托（组件树的扁平化视图），
///   由 ChatApp 按需更新（聊天区行数动态变化）。
/// - 每帧调用 <see cref="RenderFrame"/>：拉取全部行 → 逐行与上次输出比较
///   （用去 ANSI 后的可视文本比较，样式差异不触发重绘）→ 计算要重绘的行号区间。
/// - 输出策略：整帧定位到 (1,1) 不清屏，逐行用 CursorTo + 整行重写（内容行数恒定
///   为终端高度，底部留白行自动填充）。这比逐字符 diff 简单且对聊天场景足够高效。
/// </summary>
public sealed class TuiScreen
{
    private readonly TerminalDriver _terminal;
    private string[] _previous = [];
    private int _lastWidth;
    private int _lastHeight;

    public TuiScreen(TerminalDriver terminal)
    {
        _terminal = terminal;
    }

    /// <summary>当前帧的行源。返回的行数即内容高度；不足终端高度时底部补空白。</summary>
    public Func<string[]>? LayoutRoot { get; set; }

    /// <summary>渲染一帧：执行组件渲染 + 差分输出。force=true 强制全量重绘。</summary>
    public void RenderFrame(bool force = false)
    {
        var lines = LayoutRoot?.Invoke() ?? [];
        var width = _terminal.Width;
        var height = _terminal.Height;
        if (width <= 0 || height <= 0) return;

        // 布局行数固定为终端高度：不足时底部留白，超出时截断。
        // 使用布局本身的长度作为基准，避免 WindowHeight 漂移导致布局与渲染错位。
        var frame = new string[height];
        for (int i = 0; i < height; i++)
        {
            if (i < lines.Length)
            {
                frame[i] = PadLine(lines[i], width);
            }
            else
            {
                frame[i] = string.Empty; // 底部留白
            }
        }
        LastFrame = frame; // 供光标定位复用,避免二次调用 LayoutRoot

        if (force || width != _lastWidth || height != _lastHeight || _previous.Length != height)
        {
            // 尺寸变化/首帧/历史帧高度不一致：全量重绘（清屏杜绝残留）
            FullRedraw(frame, height);
            return;
        }

        // 聊天 TUI 行数少（≤终端高度），每帧全量逐行重写比逐行 diff 更简单可靠：
        // 逐行 diff 在"内容相同但行位置移动"（滚动）时容易残留旧行，
        // 且 _previous 与真实屏幕可能因多次 LayoutRoot 调用而不同步。
        // 每帧输出约 2KB，对聊天刷新频率完全可接受。
        RewriteAll(frame, height);
        _previous = frame;
    }

    /// <summary>最近一次渲染的帧（含 ANSI 样式），供 TuiApp 定位光标复用。</summary>
    public string[]? LastFrame { get; private set; }

    /// <summary>逐行重写全部行（不清屏，避免闪烁）：CursorTo + 行内容 + 清行尾。</summary>
    private void RewriteAll(string[] frame, int height)
    {
        var sb = new StringBuilder();
        sb.Append(Ansi.HideCursor);
        for (int i = 0; i < height; i++)
        {
            sb.Append(Ansi.CursorTo(i + 1, 1)).Append(frame[i]).Append(Ansi.ClearLineAfter);
        }
        _terminal.Write(sb.ToString());
        _lastWidth = _terminal.Width;
        _lastHeight = height;
    }

    private void FullRedraw(string[] frame, int height)
    {
        // 尺寸变化或首帧：先清屏再全量重绘，杜绝残留
        var sb = new StringBuilder();
        sb.Append(Ansi.HideCursor).Append(Ansi.CursorHome).Append(Ansi.ClearScreen);
        for (int i = 0; i < height; i++)
        {
            sb.Append(Ansi.CursorTo(i + 1, 1)).Append(frame[i]).Append(Ansi.ClearLineAfter);
        }
        _terminal.Write(sb.ToString());
        _previous = frame;
        _lastWidth = _terminal.Width;
        _lastHeight = height;
    }

    private static string PadLine(string line, int width)
    {
        var w = TextWidth.Measure(line);
        if (w == width) return line;
        if (w > width)
        {
            return TextWidth.Truncate(line, width, "");
        }
        return line + new string(' ', width - w);
    }

    /// <summary>定位硬件光标（供 Input 组件显示插入点）。</summary>
    public void PositionCursor(int row, int col)
    {
        _terminal.PositionCursor(row, col);
        _terminal.ShowCursor();
    }
}