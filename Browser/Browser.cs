using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.Extensions;
using OpenQA.Selenium.Support.UI;
using BrowserService.Stealth;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Globalization;
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
    public double ActualPixelScaleFactor { get; private set; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public string? BinaryPath { get; init; } = null;
    public string? ChromeDriverPath { get; init; } = null;
    public void AdaptSystem(){
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ActualPixelScaleFactor = DeviceScaleFactor;
        }
    }
}

class DriverPack
{
    public ChromiumDriver driver;
    public WebDriverWait driverWait;
    public DriverPack(ChromeDriver driver, TimeSpan waitTimeout)
    {
        this.driver = driver;
        // 等待超时与 browserOptions.Timeout 对齐（Timeout + 5s 缓冲），避免硬编码 15s
        driverWait = new WebDriverWait(driver!, waitTimeout);
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
    private Task? scriptsLoadTask;
    private readonly object scriptsLoadLock = new();
    /// <summary>
    /// 惰性加载脚本资源（首次使用时），避免构造函数同步阻塞 IO；
    /// 文件缺失时抛出带明确提示的异常。
    /// </summary>
    private Task EnsureScriptsLoadedAsync()
    {
        if (scriptsLoadTask != null)
        {
            return scriptsLoadTask;
        }
        lock (scriptsLoadLock)
        {
            scriptsLoadTask ??= LoadScripts();
            return scriptsLoadTask;
        }
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
        try
        {
            var results = await Task.WhenAll(tasks);
            jsReader = results[0];
            getSearchResult = results[1];
            preprocessWbHot = results[2];
            preprocessBingResult = results[3];
            markdownTemplate = results[4];
            mermaidJs = results[5];
            mathJaxJs = results[6];
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"加载浏览器脚本资源失败（javascript/ 目录），请确认脚本文件完整: {ex.GetBaseException().Message}", ex);
        }
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

        // 尺寸参数只添加一次（options 实例复用于多次浏览器启动），避免重启后参数重复累积
        options.AddArgument($"--window-size={this.browserOptions.Width},{this.browserOptions.Height}");
        if (this.browserOptions.DeviceScaleFactor != 1.0)
        {
            options.AddArgument($"--force-device-scale-factor={this.browserOptions.DeviceScaleFactor}");
        }
        // 脚本资源改为惰性加载（首次使用时），避免构造函数同步阻塞 IO
    }

    public Browser(bool headless) : this(new BrowserOptions { Headless = headless })
    {
    }

    private async Task<ChromeDriver> LoadBrowser()
    {
        resourceCountdown.Start();

        var driver = await Task.Run(() => StealthClient.Instantiate(options, stealthInstanceSettings));
        driver.Manage().Timeouts().PageLoad = browserOptions.Timeout;
        driver.Manage().Timeouts().AsynchronousJavaScript = browserOptions.Timeout;
        driverPack = new(driver, browserOptions.Timeout + TimeSpan.FromSeconds(5));
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
    private readonly object closeBrowserLock = new();
    private void CloseBrowser()
    {
        lock (closeBrowserLock)
        {
            if (driverPack == null)
            {
                return;
            }
            try
            {
                driverPack.driver.Quit();
            }
            catch
            {
                // 浏览器进程可能已退出，继续清理
            }
            driverPack.driver.Dispose();
            driverPack = null;
        }
    }
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private async Task UseBrowser()
    {
        if (driver == null)
        {
            // 双重检查：避免并发请求同时创建 driver
            await loadLock.WaitAsync();
            try
            {
                if (driver == null)
                {
                    await LoadBrowser();
                }
            }
            finally
            {
                loadLock.Release();
            }
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
        await EnsureScriptsLoadedAsync();
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            try
            {
                await driver!.Navigate().GoToUrlAsync(url);
                await Task.Delay(ExecuteScriptDelayTime);
                await EnsurePageLoaded();
                var result = driver.ExecuteScript(jsReader)!.ToString()!;
                return Trim(result);
            }
            finally
            {
                // 空白页导航必须在锁内等待完成，避免与下一个请求的导航并发互相取消
                try
                {
                    await GotoBlankPage();
                }
                catch
                {
                    // 清理失败不掩盖主流程结果
                }
                mutex.Release();
            }
        });

        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            // 只回传简短错误信息，不把完整异常/堆栈暴露给 LLM 或用户
            return $"页面加载失败: {ex.Message}";
        }
    }
    public async Task<byte[]> TakeMarkdownScreenshot(string md)
    {
        await EnsureScriptsLoadedAsync();
        var html = Markdown2Html.MarkdownConverter.ToHtml(md);
        // 一次性替换模板占位符：避免 html 内容里出现 {{fontScale}} 等占位符时被二次替换
        var styledHtml = Regex.Replace(markdownTemplate, @"\{\{(content|fontScale|mermaidJs|mathJaxJs)\}\}",
            match => match.Groups[1].Value switch
            {
                "content" => html,
                "fontScale" => browserOptions.FontScale.ToString(CultureInfo.InvariantCulture),
                "mermaidJs" => mermaidJs,
                "mathJaxJs" => mathJaxJs,
                _ => match.Value
            });

        return await TakeScreenshot(styledHtml);
    }
    public async Task<byte[]> TakeScreenshot(string html)
    {
        await UseBrowser();
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            try
            {
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

                    // 计算 inner 和 outer 的差距 (主要是标题栏和边框)；double 用 InvariantCulture 避免文化差异
                    var scale = browserOptions.ActualPixelScaleFactor.ToString(CultureInfo.InvariantCulture);
                    var offsetWidth = Convert.ToInt32(((IJavaScriptExecutor)driver!).ExecuteScript($"return window.outerWidth/{scale} - window.innerWidth;"));
                    var offsetHeight = Convert.ToInt32(((IJavaScriptExecutor)driver!).ExecuteScript($"return window.outerHeight/{scale} - window.innerHeight;"));

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
            }
            finally
            {
                // 页面已是 about:blank，无需再次跳转；只释放互斥锁
                mutex.Release();
            }
        });

        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            // 与 View 失败路径一致：只暴露简短错误信息
            throw new Exception($"页面截图失败: {ex.Message}");
        }
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
        await EnsureScriptsLoadedAsync();
        var url = ToStandardUri($"https://cn.bing.com/search?q={HttpUtility.UrlEncode(keyword)}&FORM=ANNTA1&adppc=EDGEXST&PC=U531" +
            (internationalVersion ? "&ensearch=1" : string.Empty));
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            try
            {
                await driver!.Navigate().GoToUrlAsync(url);
                await Task.Delay(ExecuteScriptDelayTime);
                var result = driver.ExecuteScript(getSearchResult)!.ToString()!;

                return FormatSearchResult(result);
            }
            finally
            {
                // 空白页导航在锁内等待完成，避免与下一个请求的导航并发
                try
                {
                    await GotoBlankPage();
                }
                catch
                {
                }
                mutex.Release();
            }
        });

        try
        {
            return await task;
        }
        catch
        {
            //if the script failed, try to view the page
            return await View(url);
        }
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
        await EnsureScriptsLoadedAsync();
        var url = "https://m.weibo.cn/p/106003type=25&filter_type=realtimehot";
        var query = "return document.querySelector(\"#app > div:nth-child(1) > div:nth-child(2) > div:nth-child(3) > div > div\")";
        var delayTimeout = 1500;
        var checkInterval = 400;
        var task = Task.Run(async () =>
        {
            await mutex.WaitAsync();
            try
            {
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
            }
            finally
            {
                // 空白页导航在锁内等待完成，避免与下一个请求的导航并发
                try
                {
                    await GotoBlankPage();
                }
                catch
                {
                }
                mutex.Release();
            }
        });

        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            return $"调用失败: {ex.Message}";
        }
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
        // 先停掉倒计时（防止计时器回调再次 CloseBrowser），再完整清理浏览器进程
        resourceCountdown.Dispose();
        CloseBrowser();
        GC.SuppressFinalize(this);
    }
}
