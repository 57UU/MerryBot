using CommonLib;

namespace MerryBot.Test;

/// <summary>CommonLib.RequestCaching 行为测试：命中/未命中、主动移除、过期淘汰、按权重容量上限。</summary>
public class RequestCachingTests
{
    [Fact]
    public void Set_Then_Get_ReturnsValue()
    {
        var cache = new RequestCaching(TimeSpan.FromHours(1));
        cache.SetCache("key", "value");

        Assert.True(cache.TryGetCache<string>("key", out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void Get_MissingKey_ReturnsFalse()
    {
        var cache = new RequestCaching(TimeSpan.FromHours(1));

        Assert.False(cache.TryGetCache<string>("missing", out _));
    }

    [Fact]
    public void Set_SameKey_Overwrites()
    {
        var cache = new RequestCaching(TimeSpan.FromHours(1));
        cache.SetCache("key", "first");
        cache.SetCache("key", "second");

        Assert.True(cache.TryGetCache<string>("key", out var value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void Remove_Then_Get_ReturnsFalse()
    {
        var cache = new RequestCaching(TimeSpan.FromHours(1));
        cache.SetCache("key", "value");

        cache.Remove("key");

        Assert.False(cache.TryGetCache<string>("key", out _));
    }

    [Fact]
    public void ExpiredEntry_IsNotReturned()
    {
        // 绝对过期：超过后 Get 惰性检查失效
        var cache = new RequestCaching(TimeSpan.FromMilliseconds(200));
        cache.SetCache("key", "value");
        Assert.True(cache.TryGetCache<string>("key", out _));

        Thread.Sleep(400);

        Assert.False(cache.TryGetCache<string>("key", out _));
    }

    [Fact]
    public void SizeProvider_NewEntryOverTotalLimit_IsNotCached()
    {
        // sizeLimit 为总权重上限；此处按 byte[] 长度计。
        // MemoryCache 语义：插入新条目会导致总量超限时，新条目不入缓存（旧条目保留）。
        var cache = new RequestCaching(
            TimeSpan.FromHours(1),
            sizeLimit: 100,
            sizeProvider: static value => value is byte[] bytes ? bytes.Length : 1);

        cache.SetCache("a", new byte[60]);
        cache.SetCache("b", new byte[60]); // 60 + 60 > 100 → b 不被缓存

        Assert.True(cache.TryGetCache<byte[]>("a", out _));
        Assert.False(cache.TryGetCache<byte[]>("b", out _));
    }

    [Fact]
    public void SizeProvider_SingleEntryOverLimit_IsNotCached()
    {
        var cache = new RequestCaching(
            TimeSpan.FromHours(1),
            sizeLimit: 100,
            sizeProvider: static value => value is byte[] bytes ? bytes.Length : 1);

        cache.SetCache("big", new byte[150]);

        Assert.False(cache.TryGetCache<byte[]>("big", out _));
    }

    [Fact]
    public void DefaultConstruction_CountsEachEntryAsOne()
    {
        // 未传 sizeProvider 时每条目权重 1：同字节数组也不触发权重淘汰
        var cache = new RequestCaching(TimeSpan.FromHours(1), sizeLimit: 2);

        cache.SetCache("a", new byte[1000]);
        cache.SetCache("b", new byte[1000]);
        cache.SetCache("c", new byte[1]); // 第 3 条超出条目上限 → c 不被缓存

        Assert.True(cache.TryGetCache<byte[]>("a", out _));
        Assert.True(cache.TryGetCache<byte[]>("b", out _));
        Assert.False(cache.TryGetCache<byte[]>("c", out _));
    }
}
