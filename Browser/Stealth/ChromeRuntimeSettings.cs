namespace BrowserService.Stealth;

/// <summary>
/// Chrome Runtime 伪装配置
/// </summary>
public record ChromeRuntimeSettings
{
    public bool FakeIt { get; set; } = true;
    public bool RunOnInsercureOrigins { get; set; } = false;
}