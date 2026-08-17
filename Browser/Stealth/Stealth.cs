using OpenQA.Selenium.Chromium;

namespace BrowserService.Stealth;

/// <summary>
/// 隐身模式入口 - 提供 <c>StealthClient.Instantiate()</c> 和 <c>options.ApplyStealth()</c> 方法
/// </summary>
public static class StealthClient
{
    /// <summary>
    /// 创建并配置一个带有隐身保护的 ChromiumDriver（Chrome 或 Edge 通用）
    /// </summary>
    /// <param name="options">Chromium 选项（Chrome/Edge 均可）</param>
    /// <param name="instanceSettings">隐身设置，为空则使用默认设置</param>
    /// <returns>配置后的 ChromiumDriver 实例</returns>
    public static ChromiumDriver Instantiate(ChromiumOptions? options, StealthInstanceSettings? instanceSettings = null)
    {
        return StealthService.ApplyStealth(options, instanceSettings);
    }

    /// <summary>
    /// 为 ChromiumOptions 应用隐身配置（目前仅作标记，实际配置在 Instantiate 中完成）
    /// </summary>
    public static ChromiumOptions ApplyStealth(this ChromiumOptions options)
    {
        return options;
    }
}