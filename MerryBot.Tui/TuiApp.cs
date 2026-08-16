using System.Text;
using System.Threading.Channels;

namespace Agent.Tui.Core;

/// <summary>
/// TUI 应用主循环（借鉴 pi 的 TUI 类）：
/// - 启动：进入 alt screen + raw mode + bracketed paste
/// - 后台任务持续读键入队（不混用 Console.KeyAvailable/ReadKey，统一走 KeyParser 流读取）
/// - 主循环：轮询键队列 + 刷新请求 → 渲染一帧（差分输出）→ 定位光标
/// - 退出：恢复终端
///
/// 布局由 ChatApp 提供：一个返回"完整屏幕行"的函数（含聊天区/面板/输入/状态栏）。
/// 本类负责绘制该布局并在渲染后定位硬件光标（Input 的 CursorMarker 位置）。
/// </summary>
public sealed class TuiApp : IDisposable
{
    private readonly TerminalDriver _terminal = new();
    private readonly KeyParser _keys;
    private readonly TuiScreen _screen;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    /// <summary>完整屏幕布局源：返回行数组（每行含 ANSI 样式），长度 ≤ 终端高度。</summary>
    public Func<string[]>? ScreenRoot { get; set; }

    /// <summary>聚焦组件（其 CursorMarker 会被解析为硬件光标）。</summary>
    public IFocusable? Focused { get; set; }

    /// <summary>输入未被组件消费时的全局处理（如 Ctrl+C 退出）。</summary>
    public Action<KeyEvent>? OnUnhandledInput { get; set; }

    /// <summary>可选的异步工作循环（如聊天 pump），进入主循环前启动。</summary>
    public Func<CancellationToken, Task>? BackgroundTask { get; set; }

    /// <summary>退出请求。</summary>
    public void RequestStop() => _cts.Cancel();

    public TuiApp()
    {
        _keys = new KeyParser();
        _screen = new TuiScreen(_terminal);
        _screen.LayoutRoot = () => ScreenRoot?.Invoke() ?? [];
    }

    /// <summary>无输入响应式渲染触发：供后台线程通知 UI 刷新。</summary>
    public void Invalidate() => _needsRender = true;
    private volatile bool _needsRender;

    public void Run()
    {
        if (_running) return;
        _running = true;

        _terminal.EnterAltScreen();
        RawMode.Enable();

        // 键读取任务：持续把按键投递到队列（KeyParser 阻塞读 stdin）
        var keyQueue = Channel.CreateUnbounded<KeyEvent>();
        var readTask = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var ev = _keys.Read();
                    await keyQueue.Writer.WriteAsync(ev, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (EndOfStreamException)
            {
                // stdin 关闭：正常退出
            }
            catch (Exception ex)
            {
                // 读键异常：记录并退出循环
                try { Console.Error.WriteLine($"[tui.kbd] {ex}"); } catch { }
            }
        });

        var background = BackgroundTask?.Invoke(_cts.Token);
        try
        {
            // 首帧需要建立基线：先等一帧屏幕稳定（聊天区就绪提示已入队）
            _screen.RenderFrame(force: true);
            while (!_cts.IsCancellationRequested)
            {
                // 优先消费键事件
                if (keyQueue.Reader.TryRead(out var ev))
                {
                    if (!Dispatch(ev)) break;
                    _screen.RenderFrame();
                    PositionCursorFromMarker();
                    continue;
                }
                // 无键则处理懒刷新
                if (_needsRender)
                {
                    _needsRender = false;
                    _screen.RenderFrame();
                    PositionCursorFromMarker();
                    continue;
                }
                // 二者皆无：短暂等待（让出 CPU，避免忙轮询）
                Thread.Sleep(10);
            }
        }
        finally
        {
            _cts.Cancel();
            try { readTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略 */ }
            _terminal.ShowCursor();
            RawMode.Disable();
            _terminal.LeaveAltScreen();
            if (background is not null)
            {
                try { background.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略 */ }
            }
            _running = false;
        }
    }

    private bool Dispatch(KeyEvent ev)
    {
        // 先交给聚焦组件
        if (Focused is IComponent c && c.HandleInput(ev))
        {
            return true;
        }
        // 再交全局
        if (OnUnhandledInput is { } handler)
        {
            var before = _cts.IsCancellationRequested;
            handler(ev);
            if (!before && _cts.IsCancellationRequested) return false;
            return true;
        }
        return true;
    }

    /// <summary>在最后渲染的帧里查找 CURSOR_MARKER，把硬件光标定位过去。</summary>
    private void PositionCursorFromMarker()
    {
        if (Focused is null) return;
        // 复用渲染帧(避免二次调用 ScreenRoot,且保证与落屏内容一致)
        var root = _screen.LastFrame;
        if (root is null) return;
        for (int row = 0; row < root.Length; row++)
        {
            var idx = root[row].IndexOf(Input.CursorMarker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var before = root[row][..idx];
            var col = TextWidth.Measure(before) + 1;
            _terminal.ShowCursor();
            _terminal.PositionCursor(row + 1, col);
            return;
        }
        _terminal.HideCursor();
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}