using System.Collections.ObjectModel;
using ModelsDev.Sdk.Models;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Agent.Tui.Views;

/// <summary>供应商管理窗口：增/改/删已配置供应商。</summary>
public sealed class ProviderManagerWindow : Dialog
{
    private readonly IApplication _app;
    private readonly TuiConfig _cfg;
    private readonly CatalogService _catalog;
    private readonly ObservableCollection<string> _provSource = [];
    private readonly ListView _list;
    private int _selectedIdx = -1;

    public ProviderManagerWindow(IApplication app, TuiConfig cfg, CatalogService catalog)
    {
        _app = app;
        _cfg = cfg;
        _catalog = catalog;
        Title = "供应商管理 (↑↓ 选择, Add/Edit/Remove/Done)";
        Width = 74;
        Height = 22;

        var hint = new Label { Text = "已配置供应商：", X = 0, Y = 0 };
        Add(hint);

        _list = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        _list.Source = new ListWrapper<string>(_provSource);
        Add(_list);

        var add = new Button { Text = "Add", X = 0, Y = Pos.Bottom(_list) };
        add.Accepting += (_, e) => { e.Handled = true; OnAdd(); };
        Add(add);

        var edit = new Button { Text = "Edit", X = 6, Y = Pos.Bottom(_list) };
        edit.Accepting += (_, e) => { e.Handled = true; OnEdit(); };
        Add(edit);

        var remove = new Button { Text = "Remove", X = 12, Y = Pos.Bottom(_list) };
        remove.Accepting += (_, e) => { e.Handled = true; OnRemove(); };
        Add(remove);

        var done = new Button { Text = "Done", X = 64, Y = Pos.Bottom(_list) };
        done.Accepting += (_, e) => { e.Handled = true; _app.RequestStop(); };
        Add(done);

        Rebuild();
    }

    private void Rebuild()
    {
        _provSource.Clear();
        foreach (var p in _cfg.Providers)
        {
            _provSource.Add($"{p.Name} ({p.Id})  [{p.Models.Count} models]");
        }
        _list.SelectedItem = _cfg.Providers.Count > 0 ? 0 : null;
    }

    private void CaptureSelection()
    {
        _selectedIdx = _list.SelectedItem.GetValueOrDefault(-1);
    }

    private void OnAdd()
    {
        var dlg = new ProviderEditDialog(_app, _catalog, existing: null);
        _app.Run(dlg);
        if (dlg.Result is { } p)
        {
            _cfg.Providers.Add(p);
            if (string.IsNullOrEmpty(_cfg.Active.Provider))
            {
                _cfg.Active.Provider = p.Id;
                _cfg.Active.Model = p.Models.FirstOrDefault();
            }
            Rebuild();
        }
    }

    private void OnEdit()
    {
        CaptureSelection();
        if (_selectedIdx < 0 || _selectedIdx >= _cfg.Providers.Count)
        {
            return;
        }
        var existing = _cfg.Providers[_selectedIdx];
        var dlg = new ProviderEditDialog(_app, _catalog, existing);
        _app.Run(dlg);
        if (dlg.Result is { } p)
        {
            _cfg.Providers[_selectedIdx] = p;
            // 若编辑的是活动供应商，同步活动模型（若被移除则回退首个）
            if (_cfg.Active.Provider == p.Id)
            {
                _cfg.Active.Model = p.Models.Contains(_cfg.Active.Model) ? _cfg.Active.Model : p.Models.FirstOrDefault();
            }
            Rebuild();
        }
    }

    private void OnRemove()
    {
        CaptureSelection();
        if (_selectedIdx < 0 || _selectedIdx >= _cfg.Providers.Count)
        {
            return;
        }
        var removed = _cfg.Providers[_selectedIdx];
        _cfg.Providers.RemoveAt(_selectedIdx);
        if (_cfg.Active.Provider == removed.Id)
        {
            var first = _cfg.Providers.FirstOrDefault();
            _cfg.Active.Provider = first?.Id;
            _cfg.Active.Model = first?.Models.FirstOrDefault();
        }
        Rebuild();
    }
}

/// <summary>添加/编辑单个供应商的表单：选供应商(Add)、填 api_base/api_key、勾选模型。</summary>
public sealed class ProviderEditDialog : Dialog
{
    private readonly IApplication _app;
    private readonly CatalogService _catalog;
    private readonly ProviderConfig? _existing;
    private readonly bool _isEdit;

    private readonly List<Provider> _catalogProviders;
    private string? _chosenProviderId;

    private readonly ListView _providerList;
    private readonly ObservableCollection<string> _providerSource = [];
    private readonly Button _pickBtn;

    private readonly TextField _apiBase;
    private readonly TextField _apiKey;

    private readonly ListView _modelList;
    private readonly ObservableCollection<string> _modelSource = [];
    private readonly HashSet<int> _selectedModels = []; // 索引到 _allModels
    private List<ModelInfo> _allModels = [];

    public ProviderConfig? Result { get; private set; }

    public ProviderEditDialog(IApplication app, CatalogService catalog, ProviderConfig? existing)
    {
        _app = app;
        _catalog = catalog;
        _existing = existing;
        _isEdit = existing is not null;
        Title = _isEdit ? $"编辑供应商：{existing!.Name}" : "添加供应商";
        Width = 74;
        Height = 24;

        _catalogProviders = catalog.IsLoaded ? catalog.GetAllProviders().OrderBy(p => p.Name).ToList() : [];

        var provLabel = new Label
        {
            Text = _isEdit ? "供应商（已固定）：" : "供应商目录（↑↓ 选择后点 Pick）：",
            X = 0,
            Y = 0,
        };
        Add(provLabel);

        _providerList = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = 6,
        };
        _providerList.Source = new ListWrapper<string>(_providerSource);
        Add(_providerList);

        _pickBtn = new Button { Text = "Pick", X = 0, Y = Pos.Bottom(_providerList) };
        _pickBtn.Accepting += (_, e) => { e.Handled = true; OnPickProvider(); };
        Add(_pickBtn);

        var baseLabel = new Label { Text = "API Base:", X = 0, Y = Pos.Bottom(_pickBtn) };
        Add(baseLabel);
        _apiBase = new TextField { X = 0, Y = Pos.Bottom(baseLabel), Width = Dim.Fill() };
        Add(_apiBase);

        var keyLabel = new Label { Text = "API Key:", X = 0, Y = Pos.Bottom(_apiBase) };
        Add(keyLabel);
        _apiKey = new TextField { X = 0, Y = Pos.Bottom(keyLabel), Width = Dim.Fill() };
        Add(_apiKey);

        var modelsLabel = new Label { Text = "模型（↑↓ 移动, Enter 勾选/取消）:", X = 0, Y = Pos.Bottom(_apiKey) };
        Add(modelsLabel);
        _modelList = new ListView
        {
            X = 0,
            Y = Pos.Bottom(modelsLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _modelList.Source = new ListWrapper<string>(_modelSource);
        _modelList.Accepting += (_, e) => { e.Handled = true; ToggleModel(); };
        Add(_modelList);

        var confirm = new Button { Text = "确认", X = 0, Y = Pos.Bottom(_modelList) };
        confirm.Accepting += (_, e) => { e.Handled = true; OnConfirm(); };
        Add(confirm);
        var cancel = new Button { Text = "取消", X = 6, Y = Pos.Bottom(_modelList) };
        cancel.Accepting += (_, e) => { e.Handled = true; _app.RequestStop(); };
        Add(cancel);

        InitProviderList();
        if (_isEdit)
        {
            LoadExisting();
        }
    }

    private void InitProviderList()
    {
        _providerSource.Clear();
        foreach (var p in _catalogProviders)
        {
            _providerSource.Add($"{p.Name} ({p.Id})");
        }
        _providerList.SelectedItem = _providerSource.Count > 0 ? 0 : null;
    }

    private void LoadExisting()
    {
        _chosenProviderId = _existing!.Id;
        _providerList.Enabled = false;
        _pickBtn.Enabled = false;
        _apiBase.Text = _existing.ApiBase;
        _apiKey.Text = _existing.ApiKey;
        LoadModels(_chosenProviderId);
        foreach (var mid in _existing.Models)
        {
            var idx = _allModels.FindIndex(m => m.Id == mid);
            if (idx >= 0)
            {
                _selectedModels.Add(idx);
            }
        }
        RenderModels();
    }

    private void OnPickProvider()
    {
        var idx = _providerList.SelectedItem.GetValueOrDefault(-1);
        if (idx < 0 || idx >= _catalogProviders.Count)
        {
            return;
        }
        var p = _catalogProviders[idx];
        _chosenProviderId = p.Id;
        _apiBase.Text = p.Api ?? string.Empty;
        _selectedModels.Clear();
        LoadModels(p.Id);
        RenderModels();
    }

    private void LoadModels(string providerId)
    {
        _allModels = _catalog.IsLoaded ? [.. _catalog.GetModels(providerId)] : [];
    }

    private void RenderModels()
    {
        _modelSource.Clear();
        for (int i = 0; i < _allModels.Count; i++)
        {
            _modelSource.Add(FormatModel(i, _allModels[i]));
        }
        _modelList.SelectedItem = _modelSource.Count > 0 ? 0 : null;
    }

    private string FormatModel(int i, ModelInfo m)
    {
        var mark = _selectedModels.Contains(i) ? "x" : " ";
        var flags = new List<string>();
        if (m.ToolCall) flags.Add("tool");
        if (m.Reasoning) flags.Add("reason");
        var flag = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : string.Empty;
        return $"[{mark}] {m.Id} — {m.Name}{flag}";
    }

    private void ToggleModel()
    {
        var idx = _modelList.SelectedItem.GetValueOrDefault(-1);
        if (idx < 0 || idx >= _allModels.Count)
        {
            return;
        }
        if (_selectedModels.Contains(idx))
        {
            _selectedModels.Remove(idx);
        }
        else
        {
            _selectedModels.Add(idx);
        }
        _modelSource[idx] = FormatModel(idx, _allModels[idx]);
    }

    private void OnConfirm()
    {
        if (string.IsNullOrEmpty(_chosenProviderId))
        {
            return;
        }
        if (_selectedModels.Count == 0)
        {
            return;
        }
        var provider = _catalogProviders.FirstOrDefault(p => p.Id == _chosenProviderId);
        var name = provider?.Name ?? _chosenProviderId!;
        var apiBase = string.IsNullOrWhiteSpace(_apiBase.Text)
            ? (provider?.Api ?? string.Empty)
            : _apiBase.Text;
        var models = _selectedModels
            .Select(i => _allModels[i].Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
        Result = new ProviderConfig
        {
            Id = _chosenProviderId,
            Name = name,
            ApiBase = apiBase ?? string.Empty,
            ApiKey = _apiKey.Text ?? string.Empty,
            Models = models,
        };
        _app.RequestStop();
    }
}
