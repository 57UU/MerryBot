using OpenQA.Selenium.Chrome;

namespace BrowserService.Stealth;

/// <summary>
/// 隐身模式入口 - 提供 <c>StealthClient.Instantiate()</c> 和 <c>options.ApplyStealth()</c> 方法
/// </summary>
public static class StealthClient
{
    /// <summary>
    /// 创建并配置一个带有隐身保护的 ChromeDriver
    /// </summary>
    /// <param name="chromeOptions">Chrome 选项</param>
    /// <param name="instanceSettings">隐身设置，为空则使用默认设置</param>
    /// <returns>配置后的 ChromeDriver 实例</returns>
    public static ChromeDriver Instantiate(ChromeOptions? chromeOptions, StealthInstanceSettings? instanceSettings = null)
    {
        return StealthService.ApplyStealth(chromeOptions, instanceSettings);
    }

    /// <summary>
    /// 为 ChromeOptions 应用隐身配置（目前仅作标记，实际配置在 Instantiate 中完成）
    /// </summary>
    public static ChromeOptions ApplyStealth(this ChromeOptions options)
    {
        return options;
    }
}