namespace BrowserService.Stealth;

/// <summary>
/// Stealth 实例配置
/// </summary>
public class StealthInstanceSettings
{
    public EStealthMode Mode { get; set; } = EStealthMode.SeleniumStealth;
    public bool FakeChromeApp { get; set; } = true;
    public ChromeRuntimeSettings FakeChromeRuntime { get; set; } = new();
    public bool IFrameProxy { get; set; } = true;
    public bool FakeCanPlayType { get; set; } = true;
    public bool FakePluginsAndMimeTypes { get; set; } = true;
    public bool FakeWindowOuterDimensions { get; set; } = true;
    public bool HideWebDriver { get; set; } = true;
    public bool RandomUserAgent { get; set; } = true;
    public bool RemoveCDCVariables { get; set; } = true;
    public bool FixHairline { get; set; } = true;
    public bool FakeLoadingTimes { get; set; } = true;
    public string? ChromeDriverPath { get; set; } = null;
}