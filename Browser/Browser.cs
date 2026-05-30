using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.Extensions;
using OpenQA.Selenium.Support.UI;
using SeleniumStealth.NET.Clients;
using SeleniumStealth.NET.Clients.Extensions;
using SeleniumStealth.NET.Clients.Models;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace BrowserService;

/// <summary>
/// 配置浏览器选项
/// note: windows linux差异
/// windows的实际width/height是乘上了DeviceScaleFactor
/// linux的缩放不影响width/height
/// </summary>
public record BrowserOptions
{
    public bool Headless { get; init; } = true;
    public int Width { get; set; } = 600;
    public int Height { get; set; } = 720;
    public bool AutoHeight { get; init; } = true;
    /// <summary>
    /// 设备缩放因子，值越大，图片会更清晰
    /// </summary>
    public double DeviceScaleFactor { get; init; } = 2;
    /// <summary>
    /// 字体缩放因子，值越大，字体会更大
    /// </summary>
    public double FontScale { get; init; } = 1.2;
    public double ActualPixelScaleFator { get; private set; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public string? BinaryPath { get; init; } = null;
    public string? ChromeDriverPath { get; init; } = null;
    public void AdaptSystem(){
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ActualPixelScaleFator = DeviceScaleFactor;
        }
    }
}

class DriverPack
{
    public ChromiumDriver driver;
    public WebDriverWait driverWait;
    public bool isSearchInitialized = false;
    public DriverPack(ChromeDriver driver)
    {
        this.driver = driver;
        driverWait = new WebDriverWait(driver!, TimeSpan.FromSeconds(15));
    }
}

/// <summary>
/// access web pages with headless chrome
/// </summary>
public partial class Browser : IDisposable
{
    DriverPack? driverPack;
    ChromiumDriver? driver { get { return driverPack?.driver; } }
    ChromeOptions options = new();
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    string getSearchResult = null;
    string jsReader = null, preprocessWbHot = null, preprocessBingResult = null;
    string markdownTemplate = null;
    string mermaidJs = null, mathJaxJs = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    SemaphoreSlim mutex = new(1);
    readonly BrowserOptions browserOptions;

    private static Task<string> LoadScript(string fileName)
    {
        return File.ReadAllTextAsync("./javascript/" + fileName, Encoding.UTF8);
    }
    private async Task LoadScripts()
    {
        string[] scriptFiles = [
            "readWeb.js",
            "getSearchResult2.js",
            "preprocessWbHot.js",
            "preprocessBingResult.js",
            "markdownStyle.html",
            "mermaid.min.js",
            "mathjax.min.js"
            ];
        List<Task<string>> tasks = new();
        foreach (var file in scriptFiles)
        {
            tasks.Add(LoadScript(file));
        }
        await Task.WhenAll(tasks);
        jsReader = tasks[0].Result;
        getSearchResult = tasks[1].Result;
        preprocessWbHot = tasks[2].Result;
        preprocessBingResult = tasks[3].Result;
        markdownTemplate = tasks[4].Result;
        mermaidJs = tasks[5].Result;
        mathJaxJs = tasks[6].Result;
    }
    readonly StealthInstanceSettings stealthInstanceSettings = new();
    readonly ResourceCountdown resourceCountdown;

    public Browser(BrowserOptions? browserOptions = null)
    {
        this.browserOptions = browserOptions ?? new BrowserOptions();
        this.browserOptions.AdaptSystem();
        resourceCountdown = new(CloseBrowser);

        options.ConfigureForWebScraping();

        if (this.browserOptions.Headless)
        {
            options.EnableHeadlessMode();
        }
        options.BinaryLocation = this.browserOptions.BinaryPath;
        options.ApplyStealth();

        bool isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (isLinuxArm64)
        {
            Console.WriteLine("Arch: Linux Arm64;you may need to install chromedriver manually");
            stealthInstanceSettings.ChromeDriverPath = "/usr/bin/chromedriver";
        }

        if (!string.IsNullOrEmpty(this.browserOptions.ChromeDriverPath))
        {
            stealthInstanceSettings.ChromeDriverPath = this.browserOptions.ChromeDriverPath;
        }


        LoadScripts().Wait();
    }

    public Browser(bool headless) : this(new BrowserOptions { Headless = headless })
    {
    }

    private async Task<ChromeDriver> LoadBrowser()
    {
        resourceCountdown.Start();

        options.AddArgument($"--window-size={browserOptions.Width},{browserOptions.Height}");
        if (browserOptions.DeviceScaleFactor != 1.0)
        {
            options.AddArgument($"--force-device-scale-factor={browserOptions.DeviceScaleFactor}");
        }

        var driver = await Task.Run(() => Stealth.Instantiate(options, stealthInstanceSettings));
        driver.Manage().Timeouts().PageLoad = browserOptions.Timeout;
        driver.Manage().Timeouts().AsynchronousJavaScript = browserOptions.Timeout;
        driverPack = new(driver);
        return driver;
    }
    /// <summary>
    /// wait for web page to be loaded (supports both regular pages and SPAs)
    /// </summary>
    /// <returns></returns>
    private Task EnsurePageLoaded()
    {
        return Task.Run(() =>
        {
            driverPack!.driverWait.Until(d =>
                ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")!.Equals("complete")
            );

            var hasRootElement = ((IJavaScriptExecutor)driverPack!.driver).ExecuteScript("return document.getElementById('root') !== null");
            if (hasRootElement != null && (bool)hasRootElement)
            {
                //SPA
                driverPack!.driverWait.Until(d =>
                {
                    var rootElement = d.FindElement(By.Id("root"));
                    if (rootElement == null) return false;
                    var innerHTML = ((IJavaScriptExecutor)d).ExecuteScript("return arguments[0].innerHTML", rootElement);
                    var innerHtmlStr = innerHTML?.ToString();
                    return innerHTML != null && !string.IsNullOrEmpty(innerHtmlStr) && innerHtmlStr.Trim().Length > 0;
                });
            }
        });
    }
    private void CloseBrowser()
    {
        if (driverPack == null)
        {
            return;
        }
        driverPack.driver.Quit();
        driverPack.driver.Dispose();
        driverPack = null;
    }
    private async Task UseBrowser()
    {
        if (driver == null)
        {
            await LoadBrowser();
        }
        resourceCountdown.UseResource();
    }
    private async Task GotoBlankPage()
    {
        if (driver != null)
            await driver.Navigate().GoToUrlAsync("about:blank");
    }
    static string Trim(string s)
    {
        s = s.Replace("\n", "").Replace("\r", "");
        return DuplicatedRegex().Replace(s, " ");
    }
    /// <summary>
    /// view web page
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public Task<string> View(string url)
    {
        return View(ToStandardUri(url));
    }
    /// <summary>
    /// view web page
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public async Task<string> View(Uri url)
    {
        await UseBrowser();
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            await driver!.Navigate().GoToUrlAsync(url);
            await Task.Delay(ExecuteScriptDelayTime);
            await EnsurePageLoaded();
            var result = driver.ExecuteScript(jsReader)!.ToString()!;
            var text = driver.ExecuteScript("return document.body.innerHTML")!.ToString();
            return Trim(result);
        });

        return await task.ContinueWith((t) =>
        {
            _ = GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            return $"调用失败 {t.Exception}";
        });
    }
    public async Task<byte[]> TakeMarkdownScreenshot(string md)
    {
        var html = Markdown2Html.MarkdownConverter.ToHtml(md);
        var styledHtml = markdownTemplate
            .Replace("{{content}}", html)
            .Replace("{{fontScale}}", browserOptions.FontScale.ToString())
            .Replace("{{mermaidJs}}", mermaidJs)
            .Replace("{{mathJaxJs}}", mathJaxJs);

        return await TakeScreenshot(styledHtml);
    }
    public async Task<byte[]> TakeScreenshot(string html)
    {
        await UseBrowser();
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            await driver!.Navigate().GoToUrlAsync("about:blank");
            ((IJavaScriptExecutor)driver!).ExecuteScript("document.open(); document.write(arguments[0]); document.close();", html);
            await Task.Delay(ExecuteScriptDelayTime);
            await EnsurePageLoaded();

            // 等待 Mermaid 和 MathJax 等异步渲染完成
            driverPack!.driverWait.Until(d =>
            {
                try
                {
                    var isComplete = ((IJavaScriptExecutor)d).ExecuteScript("return window.renderComplete !== false;");
                    return isComplete != null && (bool)isComplete;
                }
                catch
                {
                    return true; // 发生错误或变量未定义，则视为完成
                }
            });

            if (browserOptions.AutoHeight)
            {
                // 隐藏滚动条
                ((IJavaScriptExecutor)driver!).ExecuteScript("document.documentElement.style.overflow = 'hidden'; document.body.style.overflow = 'hidden';");
                
                // 计算 inner 和 outer 的差距 (主要是标题栏和边框)
                var offsetWidth = Convert.ToInt32(((IJavaScriptExecutor)driver!).ExecuteScript($"return window.outerWidth/{browserOptions.ActualPixelScaleFator} - window.innerWidth;"));
                var offsetHeight = Convert.ToInt32(((IJavaScriptExecutor)driver!).ExecuteScript($"return window.outerHeight/{browserOptions.ActualPixelScaleFator} - window.innerHeight;"));

                // 获取内容真实高度
                var contentHeight = Convert.ToInt32(((IJavaScriptExecutor)driver!).ExecuteScript("return Math.max(document.body.scrollHeight, document.body.offsetHeight, document.documentElement.clientHeight, document.documentElement.scrollHeight, document.documentElement.offsetHeight);"));
                
                // 补偿后的窗口大小
                driver.Manage().Window.Size = new System.Drawing.Size(browserOptions.Width + offsetWidth, contentHeight + offsetHeight);
            }
            else
            {
                driver.Manage().Window.Size = new System.Drawing.Size(browserOptions.Width, browserOptions.Height);
            }

            Screenshot screenshot = driver!.TakeScreenshot();

            return screenshot.AsByteArray;
        });


        return await task.ContinueWith((t) =>
        {
            _ = GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            throw t.Exception ?? new Exception("failed");
        });
    }
    public int ExecuteScriptDelayTime { set; get; } = 50;
    /// <summary>
    /// bing search
    /// </summary>
    /// <param name="keyword"></param>
    /// <returns>search reasult</returns>
    public async Task<string> Search(string keyword, bool internationalVersion)
    {
        await UseBrowser();
        if (!driverPack!.isSearchInitialized)
        {
            driverPack.isSearchInitialized = true;
            //先搜一下，不知道为什么第一次搜出来的东西没有相关性
            await Search("java 漏洞", false);
        }
        var url = ToStandardUri($"https://cn.bing.com/search?q={HttpUtility.UrlEncode(keyword)}&FORM=ANNTA1&adppc=EDGEXST&PC=U531" +
            (internationalVersion ? "&ensearch=1" : string.Empty));
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            await driver!.Navigate().GoToUrlAsync(url);
            await Task.Delay(ExecuteScriptDelayTime);
            var result = driver.ExecuteScript(getSearchResult)!.ToString()!;

            return FormatSearchResult(result);
        });

        return await await task.ContinueWith(async (t) =>
        {
            _ = GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            //if the script failed, try to view the page
            return await View(url);
        });
    }
    private static string FormatSearchResult(string raw)
    {
        List<SearchResult> obj = JsonSerializer.Deserialize<List<SearchResult>>(raw)!;
        StringBuilder sb = new();
        foreach (SearchResult item in obj)
        {
            sb.AppendLine($"# {item.Title}");
            sb.AppendLine($"- {item.Content}");
            sb.AppendLine($"- {item.Link}");
        }
        return sb.ToString();

    }
    public async Task<string> GetWeiboHot()
    {
        await UseBrowser();
        var url = "https://m.weibo.cn/p/106003type=25&filter_type=realtimehot";
        var query = "return document.querySelector(\"#app > div:nth-child(1) > div:nth-child(2) > div:nth-child(3) > div > div\")";
        var delayTimeout = 1500;
        var checkInterval = 400;
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            await driver!.Navigate().GoToUrlAsync(url);
            await Task.Delay(ExecuteScriptDelayTime);
            int delay = 0;
            while (true)
            {
                if (driver.ExecuteScript(query) == null)
                {
                    //wait
                    await Task.Delay(checkInterval);
                    delay += checkInterval;
                    if (delay > delayTimeout)
                    {
                        throw new TimeoutException("timeout");
                    }
                }
                else
                {
                    break;
                }
            }
            driver.ExecuteScript(preprocessWbHot);
            var result = driver.ExecuteScript(jsReader)!.ToString()!;
            return "|事件|热度|\n" + Trim(result);
        });

        return await task.ContinueWith((t) =>
        {
            _ = GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            return $"调用失败 {t.Exception}";
        });
    }
    public static Uri ToStandardUri(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("输入不能为空");

        raw = raw.Trim();

        // 1. 如果已经包含 scheme 就原样解析
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(raw, UriKind.Absolute);
        }

        // 2. 否则补 https:// 再解析
        return new Uri("http://" + raw, UriKind.Absolute);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex DuplicatedRegex();

    public void Dispose()
    {
        resourceCountdown.Dispose();
        driver?.Dispose();
        GC.SuppressFinalize(this);
    }
}
