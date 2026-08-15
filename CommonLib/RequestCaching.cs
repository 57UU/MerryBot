using Microsoft.Extensions.Caching.Memory;

namespace CommonLib;

public class RequestCaching
{
    /// <summary>缓存条目数上限；超出后 MemoryCache 按 LRU 淘汰，防止长时间运行内存无限增长。</summary>
    private const int MaxEntryCount = 10000;

    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultExpiration;
    public RequestCaching(TimeSpan defaultExpiration)
    {
        _cache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10),
            SizeLimit = MaxEntryCount
        });
        _defaultExpiration = defaultExpiration;
    }
    public bool TryGetCache<T>(string key, out T? value)
    {
        return _cache.TryGetValue(key, out value);
    }
    public void SetCache<T>(string key, T value, TimeSpan? expiration = null)
    {
        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(expiration ?? _defaultExpiration)
            .SetSize(1); // 每个条目计 1，配合 SizeLimit 实现条目数上限
        _cache.Set(key, value, cacheEntryOptions);
    }
}
