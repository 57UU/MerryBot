using Microsoft.Extensions.Caching.Memory;

namespace CommonLib;

public class RequestCaching
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _defaultExpiration;
    public RequestCaching(TimeSpan defaultExpiration)
    {
        _cache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
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
            .SetAbsoluteExpiration(expiration ?? _defaultExpiration);
        _cache.Set(key, value, cacheEntryOptions);
    }
}
