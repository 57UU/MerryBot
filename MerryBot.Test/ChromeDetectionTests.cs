using BrowserService;

namespace MerryBot.Test;

/// <summary>
/// 验证 Issue 2 修复的依赖判定：Windows 上检测 Chrome 是否可用的逻辑。
/// 通过可注入重载，确定性地覆盖如下四种组合（不依赖真实文件系统/注册表）：
///   1. CHROME_BIN 指向存在文件 + 候选路径命中  → true
///   2. CHROME_BIN 指向存在文件 + 候选路径未命中 → true（CHROME_BIN 优先）
///   3. CHROME_BIN 指向不存在文件 + 候选路径命中  → true（候选路径兜底）
///   4. CHROME_BIN 指向不存在文件 + 候选路径未命中 → false（需回退 Edge）
/// 这也间接保证了"无 Chrome → 回退 Edge"的触发条件。
/// </summary>
public sealed class ChromeDetectionTests
{
    private const string CandidatePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string ChromeBinEnv = @"C:\env\chrome.exe";

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void IsChromeAvailable_FourCombinations(
        bool chromeBinExists, bool candidateExists, bool expected)
    {
        Assert.Equal(expected, Detect(ChromeBinEnv, chromeBinExists, candidateExists));
    }

    [Fact]
    public void IsChromeAvailable_ChromeBinUnset_FallsBackToCandidate()
    {
        // CHROME_BIN 未设置（返回 null）时，仅看候选路径
        Assert.True(Detect(null, chromeBinExists: false, candidateExists: true));
        Assert.False(Detect(null, chromeBinExists: false, candidateExists: false));
    }

    [Fact]
    public void IsChromeAvailable_ChromeBinWhitespace_TreatedAsUnset()
    {
        // 仅空白的 CHROME_BIN 等同未设置
        Assert.False(Detect("   ", chromeBinExists: false, candidateExists: false));
        Assert.True(Detect("   ", chromeBinExists: false, candidateExists: true));
    }

    private static bool Detect(
        string? chromeBinEnv, bool chromeBinExists, bool candidateExists)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chromeBinEnv is not null && chromeBinExists)
        {
            existing.Add(chromeBinEnv);
        }

        if (candidateExists)
        {
            existing.Add(CandidatePath);
        }

        Func<string, bool> fileExists = existing.Contains;
        Func<string, string?> getEnv = _ => chromeBinEnv;

        return Browser.IsChromeAvailable(getEnv, fileExists, new[] { CandidatePath });
    }
}
