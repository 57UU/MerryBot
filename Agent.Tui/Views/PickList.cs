using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Agent.Tui.Views;

/// <summary>
/// 无边框内联选择器：锚定在主窗口底部，输入即过滤。
/// 单选：Enter 确认；多选：Space 勾选、Enter 确认；Esc 取消。
/// 不使用 Dialog/按钮，生命周期由调用方用 <c>Add/Remove + SetFocus</c> 托管，
/// 结果经 <see cref="WaitAsync"/> 返回（null 表示取消）。
/// </summary>
public sealed class PickList : View
{
    public sealed record Item(string Display, object? Payload);

    private const int DefaultHeight = 10;

    private readonly ObservableCollection<string> _source = [];
    private readonly List<Item> _all;
    private readonly HashSet<int> _checked = []; // 勾选索引，指向 _all
    private List<Item> _filtered;
    private readonly bool _multi;
    private readonly ListView _list;
    private readonly TextField _filter;
    private readonly TaskCompletionSource<List<Item>?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    /// <summary>null = 取消；否则为选中的项（单选一条 / 多选全部勾选项）。</summary>
    public Task<List<Item>?> WaitAsync() => _tcs.Task;

    public PickList(string title, IReadOnlyList<Item> items, bool multi = false,
        int? preSelected = null, IEnumerable<int>? preChecked = null, int height = DefaultHeight)
    {
        // 子视图（过滤框/列表）要能获得焦点，容器本身必须 CanFocus=true，
        // 否则 SetFocus 会被 SetHasFocusTrue 的父级 CanFocus 检查拒绝。
        CanFocus = true;

        _all = items.ToList();
        _filtered = _all;
        _multi = multi;
        if (preChecked is not null)
        {
            _checked.UnionWith(preChecked);
        }

        Width = Dim.Fill();
        Height = height;
        Y = Pos.AnchorEnd(height + 3); // 底部锚定，给输入行和状态栏各留一行

        _filter = new TextField
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
        };
        _filter.TextChanged += (_, _) => ApplyFilter(_filter.Text);
        _filter.Accepting += (_, e) => { e.Handled = true; Confirm(); };
        _filter.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                Close(null);
            }
        };
        Add(_filter);

        _list = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _list.Source = new ListWrapper<string>(_source);
        _list.Accepting += (_, e) => { e.Handled = true; Confirm(); };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                Close(null);
            }
            else if (_multi && key == Key.Space)
            {
                key.Handled = true;
                ToggleCurrent();
            }
        };
        // 多选模式下已勾选的行用绿色标出
        _list.RowRender += (_, e) =>
        {
            if (!_multi || e.Row < 0 || e.Row >= _filtered.Count)
            {
                return;
            }
            if (_checked.Contains(_all.IndexOf(_filtered[e.Row])))
            {
                var baseAttr = _list.GetAttributeForRole(VisualRole.Normal);
                e.RowAttribute = baseAttr with { Foreground = Color.Green };
            }
        };
        Add(_list);

        var hint = _multi
            ? $"{title} · 输入过滤 · Space 勾选 · Enter 确认 · Esc 取消"
            : $"{title} · 输入过滤 · ↑↓ 选择 · Enter 确认 · Esc 取消";
        var hintLabel = new Label { Text = hint, X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill() };
        hintLabel.SetScheme(new Scheme(new Attribute(Color.DarkGray, Color.None)));
        Add(hintLabel);

        foreach (var item in _all)
        {
            _source.Add(Render(item));
        }
        var selected = preSelected.GetValueOrDefault(0);
        _list.SelectedItem = _filtered.Count > 0 && selected >= 0 && selected < _filtered.Count ? selected : null;
    }

    /// <summary>把焦点移到过滤框，进入即输即过滤。</summary>
    public void FocusFilter() => _filter.SetFocus();

    private void ApplyFilter(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        _filtered = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(it => it.Display.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _source.Clear();
        foreach (var item in _filtered)
        {
            _source.Add(Render(item));
        }
        _list.SelectedItem = _filtered.Count > 0 ? 0 : null;
    }

    private void ToggleCurrent()
    {
        var idx = _list.SelectedItem.GetValueOrDefault(-1);
        if (idx < 0 || idx >= _filtered.Count)
        {
            return;
        }
        var item = _filtered[idx];
        var real = _all.IndexOf(item);
        if (real < 0)
        {
            return;
        }
        if (!_checked.Add(real))
        {
            _checked.Remove(real);
        }
        _source[idx] = Render(item);
    }

    private void Confirm()
    {
        if (_closed)
        {
            return;
        }
        if (!_multi)
        {
            var idx = _list.SelectedItem.GetValueOrDefault(-1);
            if (idx < 0 || idx >= _filtered.Count)
            {
                return; // 无有效选择：不关闭，Esc 取消
            }
            Close([_filtered[idx]]);
            return;
        }
        var chosen = new List<Item>();
        for (int i = 0; i < _all.Count; i++)
        {
            if (_checked.Contains(i))
            {
                chosen.Add(_all[i]);
            }
        }
        if (chosen.Count == 0)
        {
            return; // 至少勾选一个才能确认
        }
        Close(chosen);
    }

    private void Close(List<Item>? result)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _tcs.TrySetResult(result);
    }

    private string Render(Item item)
    {
        if (!_multi)
        {
            return item.Display;
        }
        var mark = _checked.Contains(_all.IndexOf(item)) ? "[x]" : "[ ]";
        return $"{mark} {item.Display}";
    }
}
