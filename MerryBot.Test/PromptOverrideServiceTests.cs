using BotPlugin;
using CommonLib;
using DataProvider;

namespace MerryBot.Test;

/// <summary>
/// PromptOverrideService（LiteDB 真实存储）测试：按群隔离、回退语义、校验与长度上限。
/// </summary>
public sealed class PromptOverrideServiceTests : IDisposable
{
    private const string SessionA = "qq/group/10001";
    private const string SessionB = "qq/group/10002";

    private readonly string _dbPath;
    private readonly PluginStorageDatabase _db;
    private readonly PromptOverrideService _service;

    public PromptOverrideServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"merrybot-test-{Guid.NewGuid():N}.db");
        _db = new PluginStorageDatabase(_dbPath);
        _service = new PromptOverrideService(_db.CreateScope("agent"));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var suffix in new[] { "", "-log", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 文件被占用则留给系统回收
                }
            }
        }
    }

    [Fact]
    public async Task GetOverride_WithoutSave_ReturnsNull()
    {
        Assert.Null(await _service.GetOverrideAsync(SessionA));
    }

    [Fact]
    public async Task SaveAndGet_RoundTrip()
    {
        await _service.SaveOverrideAsync(SessionA, "你是猫娘助手。");

        var entry = await _service.GetOverrideAsync(SessionA);
        Assert.NotNull(entry);
        Assert.Equal(SessionA, entry.SessionKey);
        Assert.Equal("你是猫娘助手。", entry.Prompt);
    }

    [Fact]
    public async Task Save_TrimsWhitespace()
    {
        await _service.SaveOverrideAsync(SessionA, "  你是猫娘助手。\n");

        var entry = await _service.GetOverrideAsync(SessionA);
        Assert.Equal("你是猫娘助手。", entry!.Prompt);
    }

    [Fact]
    public async Task Overrides_AreIsolatedBySession()
    {
        await _service.SaveOverrideAsync(SessionA, "A 的提示词");

        Assert.Null(await _service.GetOverrideAsync(SessionB));
        var sessions = await _service.ListOverridesAsync();
        Assert.Single(sessions);
        Assert.Equal(SessionA, sessions[0].SessionKey);
    }

    [Fact]
    public async Task Save_OverwritesExisting()
    {
        await _service.SaveOverrideAsync(SessionA, "旧提示词");
        await _service.SaveOverrideAsync(SessionA, "新提示词");

        var entry = await _service.GetOverrideAsync(SessionA);
        Assert.Equal("新提示词", entry!.Prompt);
        Assert.Single(await _service.ListOverridesAsync());
    }

    [Fact]
    public async Task Delete_RemovesOverride_AndIsIdempotent()
    {
        await _service.SaveOverrideAsync(SessionA, "待删除");

        Assert.True(await _service.DeleteOverrideAsync(SessionA));
        Assert.Null(await _service.GetOverrideAsync(SessionA));
        // 重复删除幂等返回 false，不抛异常
        Assert.False(await _service.DeleteOverrideAsync(SessionA));
    }

    [Fact]
    public async Task Save_BlankPrompt_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveOverrideAsync(SessionA, "   "));
    }

    [Fact]
    public async Task Save_TooLongPrompt_Throws()
    {
        var tooLong = new string('a', IPromptOverrideService.MaxPromptLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveOverrideAsync(SessionA, tooLong));
    }

    [Fact]
    public async Task Save_MaxLengthPrompt_Succeeds()
    {
        var max = new string('a', IPromptOverrideService.MaxPromptLength);
        await _service.SaveOverrideAsync(SessionA, max);

        Assert.Equal(max, (await _service.GetOverrideAsync(SessionA))!.Prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-session-key")]
    [InlineData("qq/private/123")]
    [InlineData("wx/group/123")]
    public async Task InvalidSessionKey_Throws(string sessionKey)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetOverrideAsync(sessionKey));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveOverrideAsync(sessionKey, "提示词"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.DeleteOverrideAsync(sessionKey));
    }

    [Fact]
    public void ResolveSystemPrompt_FallsBackToGlobal_WhenOverrideMissingOrBlank()
    {
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt(null, "全局"));
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt("", "全局"));
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt("   ", "全局"));
    }

    [Fact]
    public void ResolveSystemPrompt_UsesOverride_WhenPresent()
    {
        Assert.Equal("群复写", AgentPlugin.ResolveSystemPrompt("  群复写\n", "全局"));
    }
}
