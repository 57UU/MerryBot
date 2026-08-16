using Agent.Tui.Views;
using LlmBackend;
using ModelsDev.Sdk.Models;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- model selection ----------

    private sealed record ModelRow(string ProviderId, string ModelId, string Display);

    private async Task OpenModelPickerAsync(string query)
    {
        await EnsureCatalogAsync();
        var (activeProvider, activeModel) = _cfg.ResolveActive();
        var rows = BuildModelRows();
        if (rows.Count == 0)
        {
            AppendChat("sys", "还没有可用模型。先输入 /provider add 添加供应商并勾选模型。");
            return;
        }

        if (!string.IsNullOrEmpty(query))
        {
            var hits = rows
                .Where(r => r.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 1)
            {
                ApplySelection(hits[0].ProviderId, hits[0].ModelId);
                return;
            }
            AppendChat("sys", hits.Count == 0
                ? $"没有匹配 “{query}” 的模型，已打开选择列表："
                : $"“{query}” 匹配到 {hits.Count} 个模型，已打开选择列表：");
        }

        var activeIdx = rows.FindIndex(r => r.ProviderId == activeProvider?.Id && r.ModelId == activeModel);
        var items = rows.Select(r => new PickList.Item(r.Display, r)).ToList();
        var picker = new PickList("选择模型", items, preSelected: activeIdx);
        var result = await PickAsync(picker);
        if (result is { Count: 1 })
        {
            var row = (ModelRow)result[0].Payload!;
            ApplySelection(row.ProviderId, row.ModelId);
        }
    }

    private List<ModelRow> BuildModelRows()
    {
        var (activeProvider, activeModel) = _cfg.ResolveActive();
        var rows = new List<ModelRow>();
        foreach (var p in _cfg.Providers)
        {
            foreach (var m in p.Models)
            {
                var display = $"{p.Name} / {m}";
                if (_catalog.IsLoaded && _catalog.GetProvider(p.Id) is { } info
                    && info.Models.GetValueOrDefault(m) is { } modelInfo)
                {
                    display += $" — {modelInfo.Name}";
                }
                if (p.Id == activeProvider?.Id && m == activeModel)
                {
                    display = "[active] " + display;
                }
                rows.Add(new ModelRow(p.Id, m, display));
            }
        }
        return rows;
    }

    private void ApplySelection(string providerId, string modelId)
    {
        _cfg.Active.Provider = providerId;
        _cfg.Active.Model = modelId;
        var p = _cfg.FindProvider(providerId);
        UpdateBackend(p, modelId);
        TuiConfigStore.Save(_cfg);
        RefreshStatus();
        AppendChat("sys", $"已切换到 {p?.Name ?? providerId} / {modelId}。");
    }

    // ---------- provider management (inline flows) ----------

    private async Task RunProviderCommandAsync(string arg)
    {
        var parts = string.IsNullOrEmpty(arg)
            ? Array.Empty<string>()
            : arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var numArg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (sub)
        {
            case "" or "list":
                ListProviders();
                return;
            case "add":
                await RunProviderAddAsync();
                return;
            case "edit":
                await RunProviderEditAsync(numArg);
                return;
            case "models":
                await RunProviderModelsAsync(numArg);
                return;
            case "remove":
                await RunProviderRemoveAsync(numArg);
                return;
            default:
                AppendChat("sys", "用法: /provider [list | add | edit <n> | models <n> | remove <n>]");
                return;
        }
    }

    private void ListProviders()
    {
        var providers = _cfg.Providers;
        if (providers.Count == 0)
        {
            AppendChat("sys", "还没有供应商。输入 /provider add 添加。");
            return;
        }
        var (activeProvider, _) = _cfg.ResolveActive();
        for (int i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var star = p.Id == activeProvider?.Id ? " ★" : string.Empty;
            AppendChat("sys", $"{i + 1}. {p.Name} ({p.Id}) [{p.Models.Count} models]{star}");
        }
        AppendChat("sys", "子命令: add · edit <n> · models <n> · remove <n>");
    }

    private async Task RunProviderAddAsync()
    {
        await EnsureCatalogAsync();
        var catalogProviders = _catalog.IsLoaded
            ? _catalog.GetAllProviders().OrderBy(p => p.Name).ToList()
            : [];
        if (catalogProviders.Count == 0)
        {
            AppendChat("sys", "models.dev 目录不可用，无法选择供应商。");
            return;
        }

        var items = catalogProviders.Select(p => new PickList.Item($"{p.Name} ({p.Id})", p)).ToList();
        var picker = new PickList("选择供应商", items);
        var picked = await PickAsync(picker);
        if (picked is not { Count: 1 })
        {
            return;
        }
        var provider = (Provider)picked[0].Payload!;

        var apiBase = await PromptAsync("API Base（回车用默认）: ", provider.Api ?? string.Empty);
        if (apiBase is null)
        {
            return;
        }
        var apiKey = await PromptAsync("API Key: ", string.Empty);
        if (apiKey is null)
        {
            return;
        }

        var models = _catalog.IsLoaded ? _catalog.GetModels(provider.Id) : Array.Empty<ModelInfo>();
        if (models.Count == 0)
        {
            AppendChat("sys", $"目录中 {provider.Name} 没有可用模型。");
            return;
        }
        var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
        var modelPicker = new PickList("勾选模型", modelItems, multi: true);
        var chosen = await PickAsync(modelPicker);
        if (chosen is not { Count: > 0 })
        {
            return;
        }

        var config = new ProviderConfig
        {
            Id = provider.Id,
            Name = provider.Name,
            ApiBase = apiBase,
            ApiKey = apiKey,
            Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList(),
        };
        _cfg.Providers.Add(config);
        if (string.IsNullOrEmpty(_cfg.Active.Provider))
        {
            _cfg.Active.Provider = config.Id;
            _cfg.Active.Model = config.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已添加供应商 {config.Name}（{config.Models.Count} 个模型）。");
    }

    private async Task RunProviderEditAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var existing = _cfg.Providers[idx];

        await EnsureCatalogAsync();

        var apiBase = await PromptAsync("API Base（回车不变）: ", existing.ApiBase);
        if (apiBase is null)
        {
            return;
        }
        var apiKey = await PromptAsync("API Key（回车不变）: ", existing.ApiKey);
        if (apiKey is null)
        {
            return;
        }

        var models = _catalog.IsLoaded ? _catalog.GetModels(existing.Id) : Array.Empty<ModelInfo>();
        if (models.Count > 0)
        {
            var preChecked = new List<int>();
            for (int i = 0; i < models.Count; i++)
            {
                if (existing.Models.Contains(models[i].Id))
                {
                    preChecked.Add(i);
                }
            }
            var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
            var modelPicker = new PickList("勾选模型", modelItems, multi: true, preChecked: preChecked);
            var chosen = await PickAsync(modelPicker);
            if (chosen is null)
            {
                return;
            }
            if (chosen.Count > 0)
            {
                existing.Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList();
            }
        }
        else
        {
            AppendChat("sys", "目录不可用，模型保持不变。");
        }

        existing.ApiBase = apiBase;
        existing.ApiKey = apiKey;
        if (_cfg.Active.Provider == existing.Id)
        {
            _cfg.Active.Model = existing.Models.Contains(_cfg.Active.Model ?? string.Empty)
                ? _cfg.Active.Model
                : existing.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已更新供应商 {existing.Name}。");
    }

    private async Task RunProviderModelsAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var existing = _cfg.Providers[idx];

        await EnsureCatalogAsync();
        var models = _catalog.IsLoaded ? _catalog.GetModels(existing.Id) : Array.Empty<ModelInfo>();
        if (models.Count == 0)
        {
            AppendChat("sys", "目录不可用或该供应商没有模型。");
            return;
        }

        var preChecked = new List<int>();
        for (int i = 0; i < models.Count; i++)
        {
            if (existing.Models.Contains(models[i].Id))
            {
                preChecked.Add(i);
            }
        }
        var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
        var picker = new PickList("勾选模型", modelItems, multi: true, preChecked: preChecked);
        var chosen = await PickAsync(picker);
        if (chosen is null)
        {
            return;
        }
        if (chosen.Count == 0)
        {
            AppendChat("sys", "至少勾选一个模型。");
            return;
        }
        existing.Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList();
        if (_cfg.Active.Provider == existing.Id)
        {
            _cfg.Active.Model = existing.Models.Contains(_cfg.Active.Model ?? string.Empty)
                ? _cfg.Active.Model
                : existing.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已更新 {existing.Name} 的模型列表。");
    }

    private async Task RunProviderRemoveAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var target = _cfg.Providers[idx];

        var confirm = await PromptAsync($"确认删除 {target.Name} ({target.Id})? (y/N): ", string.Empty);
        if (confirm is null)
        {
            return;
        }
        if (!confirm.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            AppendChat("sys", "已取消删除。");
            return;
        }
        _cfg.Providers.RemoveAt(idx);
        if (_cfg.Active.Provider == target.Id)
        {
            var first = _cfg.Providers.FirstOrDefault();
            _cfg.Active.Provider = first?.Id;
            _cfg.Active.Model = first?.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已删除供应商 {target.Name}。");
    }

    private void SaveAndSync()
    {
        var (p, m) = _cfg.ResolveActive();
        UpdateBackend(p, m);
        TuiConfigStore.Save(_cfg);
        RefreshStatus();
    }

    /// <summary>按供应商配置热替换 Client 的后端（下一次请求生效），保持重试与会话不变。</summary>
    private void UpdateBackend(ProviderConfig? p, string? modelId)
        => _llmClient.UpdateBackend(new ChatCompletionBackend(
            p?.ApiBase ?? string.Empty,
            p?.ApiKey ?? string.Empty,
            modelId));

    private static int ParseIndex(string numArg)
        => int.TryParse(numArg, out var n) && n >= 1 ? n - 1 : -1;

    /// <summary>
    /// 加载 models.dev 目录；未加载时给用户一个"正在加载"的提示，避免长时间无反馈。
    /// 已加载则直接返回。
    /// </summary>
    private async Task EnsureCatalogAsync()
    {
        if (_catalog.IsLoaded)
        {
            return;
        }
        AppendChat("sys", "正在加载 models.dev 目录…");
        try
        {
            await _catalog.EnsureLoadedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendChat("error", $"加载 models.dev 目录失败：{ex.Message}");
            return;
        }
        if (_catalog.IsLoaded)
        {
            var count = _catalog.GetAllProviders().Count;
            AppendChat("sys", $"models.dev 目录已就绪（{count} 个供应商）。");
        }
        else
        {
            AppendChat("sys", "models.dev 目录不可用。可输入 /refresh 重新拉取。");
        }
    }
}
