using System.Text;

namespace Agent.Tui.Core;

/// <summary>
/// 过滤选择列表（借鉴 pi 的 SelectList 组件）：
/// - 输入即过滤（过滤框收集普通字符，其余键控制选择）
/// - ↑↓ 移动选择，Enter 确认，Esc 取消
/// - 可作为主视图渲染，也可作为覆盖层渲染（由 ChatApp 决定摆放行）
/// </summary>
public sealed class SelectList<T> : ComponentBase, IFocusable
{
    public sealed record Item(string Display, T Payload, bool Checked = false);

    private readonly List<Item> _all;
    private List<Item> _filtered;
    private string _filter = string.Empty;
    private int _selected;
    private readonly bool _multi;
    private readonly HashSet<int> _checked = []; // 指向 _all 的索引
    private readonly int _maxVisible;
    private readonly string _title;
    private readonly string _filterPrompt;

    private bool _focused;
    public bool IsFocused { get => _focused; set => _focused = value; }

    /// <summary>确认时回调（null 表示取消）。</summary>
    public Action<List<Item>?>? OnDone;

    public SelectList(string title, IEnumerable<Item> items, bool multi = false,
        int? preSelected = null, IEnumerable<int>? preChecked = null,
        int maxVisible = 8)
    {
        _title = title;
        _multi = multi;
        _maxVisible = maxVisible;
        _all = items.ToList();
        _filtered = _all;
        _selected = Math.Clamp(preSelected ?? 0, 0, Math.Max(0, _filtered.Count - 1));
        if (preChecked is not null)
        {
            _checked.UnionWith(preChecked);
        }
        _filterPrompt = multi
            ? $"{title} · 输入过滤 · Space 勾选 · Enter 确认 · Esc 取消"
            : $"{title} · 输入过滤 · ↑↓ 选择 · Enter 确认 · Esc 取消";
    }

    /// <summary>当前可见项。</summary>
    public IReadOnlyList<Item> Items => _filtered;

    public override void Invalidate() { }

    public override bool HandleInput(KeyEvent ev)
    {
        if (ev.Paste is { } paste)
        {
            AppendFilter(paste);
            return true;
        }
        switch (ev.Key)
        {
            case Key.Char:
                if (ev.Ctrl && ev.Char is 'w' or '\x15')
                {
                    _filter = string.Empty;
                    RebuildFilter();
                    return true;
                }
                if (ev.Ctrl && ev.Char is 'c' or '\x03')
                {
                    OnDone?.Invoke(null);
                    return true;
                }
                if (_multi && ev.Char == ' ')
                {
                    ToggleCurrent();
                    return true;
                }
                if (!ev.Ctrl && ev.Char >= 0x20 && ev.Char != 0x7f)
                {
                    AppendFilter(ev.Char.ToString());
                    return true;
                }
                return false;
            case Key.Backspace:
                if (_filter.Length > 0)
                {
                    _filter = _filter[..^1];
                    RebuildFilter();
                    return true;
                }
                return true;
            case Key.Up:
                MoveSelection(-1);
                return true;
            case Key.Down:
                MoveSelection(1);
                return true;
            case Key.PageUp:
                MoveSelection(-_maxVisible);
                return true;
            case Key.PageDown:
                MoveSelection(_maxVisible);
                return true;
            case Key.Home:
                _selected = 0;
                return true;
            case Key.End:
                _selected = Math.Max(0, _filtered.Count - 1);
                return true;
            case Key.Enter:
                Confirm();
                return true;
            case Key.Escape:
                OnDone?.Invoke(null);
                return true;
            default:
                return false;
        }
    }

    private void AppendFilter(string s)
    {
        // 安全:过滤输入可能来自粘贴,剥离 ESC
        _filter += Ansi.StripAnsi(s ?? string.Empty).ToString();
        RebuildFilter();
    }

    private void RebuildFilter()
    {
        var q = _filter.Trim();
        _filtered = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(it => it.Display.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
        if (_filtered.Count > 0)
        {
            // 保持选择项可见
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        _selected = (_selected + delta + _filtered.Count) % _filtered.Count;
    }

    private void Confirm()
    {
        if (_multi)
        {
            var chosen = new List<Item>();
            for (int i = 0; i < _all.Count; i++)
            {
                if (_checked.Contains(i)) chosen.Add(_all[i]);
            }
            OnDone?.Invoke(chosen.Count > 0 ? chosen : null);
            return;
        }
        if (_filtered.Count == 0 || _selected < 0 || _selected >= _filtered.Count)
        {
            OnDone?.Invoke(null);
            return;
        }
        OnDone?.Invoke([_filtered[_selected]]);
    }

    /// <summary>勾选/取消当前项（多选模式，空格触发）。</summary>
    private void ToggleCurrent()
    {
        if (_selected < 0 || _selected >= _filtered.Count) return;
        var real = _all.IndexOf(_filtered[_selected]);
        if (!_checked.Add(real)) _checked.Remove(real);
    }

    public override string[] Render(int width)
    {
        var lines = new List<string>();
        // 第一行：过滤输入 + 标题提示（灰字）
        var filterLine = TextWidth.Truncate(_filter, Math.Max(1, width - 2), "");
        lines.Add(Ansi.Dim + "🔍 " + _title + "  " + filterLine + Ansi.Reset);

        if (_filtered.Count == 0)
        {
            lines.Add(Ansi.Dim + "  (无匹配项，Esc 取消)" + Ansi.Reset);
        }
        else
        {
            var start = Math.Clamp(_selected - _maxVisible / 2, 0, Math.Max(0, _filtered.Count - _maxVisible));
            var end = Math.Min(start + _maxVisible, _filtered.Count);
            for (int i = start; i < end; i++)
            {
                var item = _filtered[i];
                var isSel = i == _selected;
                var mark = _multi ? (_checked.Contains(_all.IndexOf(item)) ? "[x] " : "[ ] ") : string.Empty;
                var arrow = isSel ? "→ " : "  ";
                // 安全:Display 可能来自模型目录等外部数据,剥离 ESC 防注入
                var text = mark + Ansi.StripAnsi(item.Display ?? string.Empty).ToString();
                var colored = isSel
                    ? Ansi.Wrap(Ansi.Reverse, arrow + TextWidth.Truncate(text, Math.Max(1, width - arrow.Length), "…"))
                    : arrow + TextWidth.Truncate(text, Math.Max(1, width - arrow.Length), "…");
                lines.Add(colored);
            }
            // 滚动指示
            if (start > 0 || end < _filtered.Count)
            {
                lines.Add(Ansi.Dim + $"  ({_selected + 1}/{_filtered.Count})" + Ansi.Reset);
            }
        }

        // 尾部提示行
        lines.Add(Ansi.Dim + _filterPrompt + Ansi.Reset);
        return lines.ToArray();
    }
}