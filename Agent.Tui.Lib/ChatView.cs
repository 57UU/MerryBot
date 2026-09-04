using System.Text;

namespace Agent.Tui.Lib;

/// <summary>
/// 聊天区滚动容器：
/// - 内容行数组由外部维护（ChatApp 的 _chatSource 等价物），本组件仅做视口切片
/// - 默认跟随底部（新内容自动滚到底）；用户向上滚动后暂停跟随，滚回底部恢复
/// - 支持 PageUp/PageDown/Home/End 滚动
///
/// 线程安全:写方(Agent 线程 OnAgentLog/聊天 pump)与读方(渲染线程 RenderViewport)
/// 并发访问,内部用锁保护 _lines 及滚动状态,防止 List 并发扩容损坏。
/// </summary>
public sealed class ChatView : ComponentBase
{
    private readonly object _sync = new();
    private readonly List<string> _lines = [];
    private int _scrollTop;      // 视口首行的内容索引
    private int _viewportHeight; // 最近一次渲染的视口高度
    private bool _followEnd = true;

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>追加一行（自动跟随底部时滚动到新内容）。</summary>
    public void Append(string line)
    {
        lock (_sync)
        {
            _lines.Add(line);
            if (_followEnd)
            {
                _scrollTop = Math.Max(0, _lines.Count - _viewportHeight);
            }
        }
    }

    /// <summary>追加多行。</summary>
    public void AppendRange(IEnumerable<string> lines)
    {
        foreach (var l in lines) Append(l);
    }

    /// <summary>就地更新一行（返回旧值；行不存在时追加）。</summary>
    public void SetLine(int index, string line)
    {
        lock (_sync)
        {
            if (index < 0) return;
            if (index >= _lines.Count)
            {
                while (_lines.Count <= index) _lines.Add(string.Empty);
            }
            _lines[index] = line;
            if (_followEnd)
            {
                _scrollTop = Math.Max(0, _lines.Count - _viewportHeight);
            }
        }
    }

    /// <summary>移除从 index 开始的所有行（截断尾部）。</summary>
    public void TruncateFrom(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _lines.Count) return;
            _lines.RemoveRange(index, _lines.Count - index);
            if (_followEnd)
            {
                _scrollTop = Math.Max(0, _lines.Count - _viewportHeight);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            _scrollTop = 0;
            _followEnd = true;
        }
    }

    public int LineCount
    {
        get { lock (_sync) return _lines.Count; }
    }

    public override void Invalidate() { }

    public override bool HandleInput(KeyEvent ev)
    {
        switch (ev.Key)
        {
            case Key.PageUp:
                ScrollBy(-_viewportHeight);
                return true;
            case Key.PageDown:
                ScrollBy(_viewportHeight);
                return true;
            case Key.Home:
                ScrollToTop();
                return true;
            case Key.End:
                ScrollToEnd();
                return true;
            default:
                return false;
        }
    }

    public void ScrollBy(int delta)
    {
        lock (_sync)
        {
            _scrollTop = Math.Clamp(_scrollTop + delta, 0, Math.Max(0, _lines.Count - _viewportHeight));
            _followEnd = _scrollTop >= Math.Max(0, _lines.Count - _viewportHeight);
        }
    }

    public void ScrollToTop()
    {
        lock (_sync)
        {
            _scrollTop = 0;
            _followEnd = false;
        }
    }

    public void ScrollToEnd()
    {
        lock (_sync)
        {
            _scrollTop = Math.Max(0, _lines.Count - _viewportHeight);
            _followEnd = true;
        }
    }

    public override string[] Render(int width)
    {
        // 组件契约实现:ChatApp 直接调 RenderViewport(带视口高度),此处兜底用终端高度。
        return RenderViewport(width, Console.WindowHeight);
    }

    /// <summary>按给定视口高度渲染内容视口（加锁快照）。</summary>
    public string[] RenderViewport(int width, int viewportHeight)
    {
        lock (_sync)
        {
            _viewportHeight = viewportHeight;
            if (_lines.Count == 0) return new string[viewportHeight];
            _scrollTop = Math.Clamp(_scrollTop, 0, Math.Max(0, _lines.Count - viewportHeight));
            // 跟随底部模式：始终贴底
            if (_followEnd)
            {
                _scrollTop = Math.Max(0, _lines.Count - viewportHeight);
            }
            var result = new string[viewportHeight];
            for (int i = 0; i < viewportHeight; i++)
            {
                var idx = _scrollTop + i;
                result[i] = idx < _lines.Count ? _lines[idx] : string.Empty;
            }
            return result;
        }
    }
}