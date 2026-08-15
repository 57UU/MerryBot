using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Agent.Tui.Views;

/// <summary>模型选择对话框：列出所有已配置供应商下的模型，选一个激活。</summary>
public sealed class ModelPickerDialog : Dialog
{
    public sealed record Row(string ProviderId, string ModelId, string Display);

    private readonly IApplication _app;
    private readonly TuiConfig _cfg;
    private readonly CatalogService _catalog;
    private readonly ObservableCollection<string> _source = [];
    private List<Row> _rows;
    private readonly ListView _list;
    private readonly TextField _filter;

    /// <summary>用户确认后的选择；null 表示取消。</summary>
    public (string ProviderId, string ModelId)? Selected { get; private set; }

    public ModelPickerDialog(IApplication app, TuiConfig cfg, CatalogService catalog)
    {
        _app = app;
        _cfg = cfg;
        _catalog = catalog;
        Title = "选择模型 (↑↓ 选择, Enter 或 OK 确认, Esc 取消)";
        Width = 72;
        Height = 20;

        var (activeProvider, activeModel) = _cfg.ResolveActive();

        _rows = BuildRows(activeProvider?.Id, activeModel);

        _filter = new TextField
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
        };
        _filter.Accepting += (_, e) =>
        {
            e.Handled = true;
            ApplyFilter(_filter.Text);
        };
        Add(_filter);

        _list = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _list.Source = new ListWrapper<string>(_source);
        _list.Accepting += (_, e) =>
        {
            e.Handled = true;
            Confirm();
        };
        Add(_list);

        var ok = new Button { Text = "OK", X = 0, Y = Pos.Bottom(_list) };
        ok.Accepting += (_, e) => { e.Handled = true; Confirm(); };
        Add(ok);

        var cancel = new Button { Text = "Cancel", X = Pos.Right(ok) + 1, Y = Pos.Bottom(_list) };
        cancel.Accepting += (_, e) => { e.Handled = true; _app.RequestStop(); };
        Add(cancel);

        foreach (var d in _rows)
        {
            _source.Add(d.Display);
        }
        SelectActiveIndex(activeProvider?.Id, activeModel);
    }

    private List<Row> BuildRows(string? activeProviderId, string? activeModelId)
    {
        var rows = new List<Row>();
        foreach (var p in _cfg.Providers)
        {
            foreach (var m in p.Models)
            {
                string display = $"{p.Name} / {m}";
                if (_catalog.IsLoaded && _catalog.GetProvider(p.Id) is { } provider
                    && provider.Models.GetValueOrDefault(m) is { } info)
                {
                    display += $" — {info.Name}";
                }
                bool isActive = p.Id == activeProviderId && m == activeModelId;
                if (isActive)
                {
                    display = "[active] " + display;
                }
                rows.Add(new Row(p.Id, m, display));
            }
        }
        return rows;
    }

    private void SelectActiveIndex(string? providerId, string? modelId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].ProviderId == providerId && _rows[i].ModelId == modelId)
            {
                _list.SelectedItem = i;
                return;
            }
        }
        _list.SelectedItem = _rows.Count > 0 ? 0 : null;
    }

    private void ApplyFilter(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        _source.Clear();
        foreach (var r in _rows)
        {
            if (string.IsNullOrEmpty(q)
                || r.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.ModelId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.ProviderId.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _source.Add(r.Display);
            }
        }
        // 过滤后行索引与原 _rows 不再对齐，需要为 ListView 重建映射
        _filteredRows = _rows
            .Where(r => string.IsNullOrEmpty(q)
                || r.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.ModelId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.ProviderId.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _list.SelectedItem = _filteredRows.Count > 0 ? 0 : null;
    }

    private List<Row> _filteredRows;

    private void Confirm()
    {
        var idx = _list.SelectedItem.GetValueOrDefault(-1);
        var rows = _filteredRows ?? _rows;
        if (idx < 0 || idx >= rows.Count)
        {
            _app.RequestStop();
            return;
        }
        var r = rows[idx];
        Selected = (r.ProviderId, r.ModelId);
        _app.RequestStop();
    }
}
