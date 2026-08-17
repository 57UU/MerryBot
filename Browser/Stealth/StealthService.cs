using System.Collections.Generic;
using System.Linq;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;

namespace BrowserService.Stealth;

/// <summary>
/// 隐身模式服务 - 为 ChromiumDriver（Chrome/Edge 通用）提供反检测和隐身保护
/// </summary>
internal static class StealthService
{
    /// <summary>
    /// 创建并配置一个带有隐身保护的 ChromiumDriver
    /// </summary>
    /// <param name="chromiumOptions">Chromium 选项（Chrome/Edge 均可），为空则使用默认选项</param>
    /// <param name="instanceSettings">隐身设置，为空则使用默认设置</param>
    /// <returns>配置后的 ChromiumDriver 实例</returns>
    public static ChromiumDriver ApplyStealth(ChromiumOptions? chromiumOptions, StealthInstanceSettings? instanceSettings)
    {
        instanceSettings ??= new StealthInstanceSettings();
        chromiumOptions ??= new ChromeOptions();

        ChromiumDriver driver;
        if (chromiumOptions is EdgeOptions edgeOptions)
        {
            // Windows 无 Chrome 回退到 Edge：由 Selenium Manager 自动定位 msedgedriver（忽略 ChromeDriverPath）
            driver = new EdgeDriver(edgeOptions);
        }
        else
        {
            var chromeOptions = chromiumOptions as ChromeOptions ?? new ChromeOptions();
            driver = string.IsNullOrWhiteSpace(instanceSettings.ChromeDriverPath)
                ? new ChromeDriver(chromeOptions)
                : new ChromeDriver(instanceSettings.ChromeDriverPath, chromeOptions);
        }

        if (instanceSettings.Mode == EStealthMode.SeleniumStealth)
        {
            EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_RequiredUtilityPack);

            if (instanceSettings.FakeChromeApp)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_FakeChromeApp);

            if (instanceSettings.FakeChromeRuntime.FakeIt)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_FakeChromeRuntime, instanceSettings.FakeChromeRuntime.RunOnInsercureOrigins);

            if (instanceSettings.IFrameProxy)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_iFrameProxy);

            if (instanceSettings.FakeCanPlayType)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_FakeCanPlayType);

            if (instanceSettings.FakePluginsAndMimeTypes)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_FakePluginsAndMimes);

            if (instanceSettings.FakeWindowOuterDimensions)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_FakeWindowOuterDimensions);

            if (instanceSettings.HideWebDriver)
                EvaluateOnNewDocument(driver, JsFunctions.SeleniumStealth_HideWebDriver);
        }
        else
        {
            EvaluateOnNewDocument(driver, JsFunctions.UndetectedChromeDriver);
        }

        EvaluateOnNewDocument(driver, JsFunctions.FakeMouseMovement);

        if (instanceSettings.RandomUserAgent)
        {
            var navigatorInfo = new NavigatorInfo();
            driver.ExecuteCdpCommand("Network.setUserAgentOverride", new Dictionary<string, object?>
            {
                { "userAgent", navigatorInfo.UserAgent }
            });
            EvaluateOnNewDocument(driver, JsFunctions.WebGlVendor, navigatorInfo.WebGLVendor, navigatorInfo.WebGLRenderer);
            EvaluateOnNewDocument(driver, JsFunctions.NavigatorVendor, navigatorInfo.Vendor);
            EvaluateOnNewDocument(driver, JsFunctions.SetDeviceMemory, navigatorInfo.MemorySize);
        }

        if (instanceSettings.RemoveCDCVariables)
            EvaluateOnNewDocument(driver, JsFunctions.RemoveCdcVariables);

        if (instanceSettings.FixHairline)
            EvaluateOnNewDocument(driver, JsFunctions.FixHairline);

        if (instanceSettings.FakeLoadingTimes)
            EvaluateOnNewDocument(driver, JsFunctions.FakeLoadingTimes);

        return driver;
    }

    private static void EvaluateOnNewDocument(ChromiumDriver driver, string jsFunction, params object[] @params)
    {
        var source = EvaluateString(jsFunction, @params);
        driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object?>
        {
            { "source", source }
        });
    }

    private static string EvaluateString(string jsFunction, params object[] @params)
    {
        var args = string.Join("', '", @params.Select(x => $"{x ?? "undefined"}"));
        return $"({jsFunction})('{args}')";
    }
}