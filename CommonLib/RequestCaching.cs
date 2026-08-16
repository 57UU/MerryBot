using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics.CodeAnalysis;

namespace CommonLib;

public class RequestCaching
{
    /// <summary>默认总权重上限；sizeProvider 未传时每条目权重为 1，该值即条目数上限。</summary>
    private const long DefaultSizeLimit = 10000;

    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultExpiration;
    private readonly Func<object, long>? _sizeProvider;

    /// <param name="sizeLimit">缓存总权重上限（超出按 LRU 淘汰）；传 <paramref name="sizeProvider"/> 时以自定义权重计（如资源字节数），否则每条目计 1。</param>
    public RequestCaching(TimeSpan defaultExpiration, long sizeLimit = DefaultSizeLimit, Func<object, long>? sizeProvider = null)
    {
        _cache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10),
            SizeLimit = sizeLimit
        });
        _defaultExpiration = defaultExpiration;
        _sizeProvider = sizeProvider;
    }
    public bool TryGetCache<T>(string key, [NotNullWhen(true)] out T? value)
    {
        return _cache.TryGetValue(key, out value);
    }
    public void SetCache<T>(string key, T value, TimeSpan? expiration = null)
    {
        var size = _sizeProvider?.Invoke(value!) ?? 1;
        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(expiration ?? _defaultExpiration)
            .SetSize(size); // 默认每条目计 1（条目数上限）；可传 sizeProvider 按值权重计（如资源字节数）
        _cache.Set(key, value, cacheEntryOptions);
    }
    /// <summary>移除指定条目（如数据失效/撤回时主动作废缓存）。</summary>
    public void Remove(string key)
    {
        _cache.Remove(key);
    }
}
