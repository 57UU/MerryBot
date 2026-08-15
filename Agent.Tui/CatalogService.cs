using ModelsDev.Sdk;
using ModelsDev.Sdk.Models;

namespace Agent.Tui;

/// <summary>
/// models.dev 目录的加载/缓存服务。优先用本地 <c>models-dev-cache.json</c>，
/// 缺失时联网拉取并落盘；<see cref="RefreshAsync"/> 强制刷新。
/// </summary>
public sealed class CatalogService
{
    private const string CacheFile = "models-dev-cache.json";
    private readonly ModelsDevClient _client = new();

    public bool IsLoaded => _client.IsLoaded;
    public static string CachePath => System.IO.Path.Combine(AppContext.BaseDirectory, CacheFile);

    /// <summary>未加载时优先读缓存，缓存缺失则联网拉取并写盘。</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_client.IsLoaded)
        {
            return;
        }
        if (File.Exists(CachePath))
        {
            try
            {
                _client.LoadFromJson(await File.ReadAllTextAsync(CachePath, cancellationToken));
                return;
            }
            catch
            {
                // 缓存损坏 → 回退到联网
            }
        }
        var json = await _client.DownloadAsync(cancellationToken);
        _client.LoadFromJson(json);
        try
        {
            await File.WriteAllTextAsync(CachePath, json, cancellationToken);
        }
        catch
        {
            // 写盘失败不影响内存使用
        }
    }

    /// <summary>删缓存后重新联网拉取。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }
        }
        catch
        {
            // 删除失败忽略
        }
        var json = await _client.DownloadAsync(cancellationToken);
        _client.LoadFromJson(json);
        try
        {
            await File.WriteAllTextAsync(CachePath, json, cancellationToken);
        }
        catch
        {
            // 写盘失败忽略
        }
    }

    public IReadOnlyList<Provider> GetAllProviders() => _client.GetAllProviders();
    public IReadOnlyList<ModelInfo> GetModels(string providerId) => _client.GetModels(providerId);
    public Provider? GetProvider(string providerId) => _client.GetProvider(providerId);
}
