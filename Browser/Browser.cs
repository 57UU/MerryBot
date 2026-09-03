using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using BrowserService.Stealth;
using System.Runtime.InteropServices;
using System.Text;

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
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
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
    public DriverPack(ChromiumDriver driver, TimeSpan waitTimeout)
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
    private static readonly Lazy<Browser> lazyInstance = new(
        () => new Browser(new BrowserOptions { BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN") }),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 进程内唯一的浏览器实例：所有调用方共享，串行复用同一个 driver。
    /// 生命周期归进程所有，调用方不得 Dispose；进程退出时自动回收。
    /// </summary>
    public static Browser Instance => lazyInstance.Value;

    static Browser()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            if (lazyInstance.IsValueCreated)
            {
                lazyInstance.Value.Dispose();
            }
        };
    }

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
    readonly bool _useEdge;

    private static Task<string> LoadScript(string fileName)
        => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "javascript", fileName), Encoding.UTF8);
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
    private readonly CommonLib.ISimpleLogger _logger;

    public Browser(BrowserOptions? browserOptions = null, CommonLib.ISimpleLogger? logger = null)
    {
        this.browserOptions = browserOptions ?? new BrowserOptions();
        this.browserOptions.AdaptSystem();
        _logger = logger ?? CommonLib.SimpleLog.Default;
        resourceCountdown = new(CloseBrowser);

        ConfigureCommonOptions(options, this.browserOptions);
        options.BinaryLocation = this.browserOptions.BinaryPath;

        // Windows 上若未检测到 Chrome，则回退使用系统自带的 Edge（同为 Chromium 内核）
        _useEdge = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsChromeAvailable();

        bool isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (isLinuxArm64)
        {
            _logger.Info("Arch: Linux Arm64;you may need to install chromedriver manually");
            stealthInstanceSettings.ChromeDriverPath = "/usr/bin/chromedriver";
        }

        if (!string.IsNullOrEmpty(this.browserOptions.ChromeDriverPath))
        {
            stealthInstanceSettings.ChromeDriverPath = this.browserOptions.ChromeDriverPath;
        }
        // 窗口尺寸/设备缩放参数已在 ConfigureCommonOptions 中添加到 options（Chrome 路径），
        // Edge 路径在 LoadBrowser 内重建选项时同样会添加；options 实例复用于多次启动，仅此一次。
        // 脚本资源改为惰性加载（首次使用时），避免构造函数同步阻塞 IO
    }

    public Browser(bool headless) : this(new BrowserOptions { Headless = headless })
    {
    }

    private async Task<ChromiumDriver> LoadBrowser()
    {
        resourceCountdown.Start();

        ChromiumOptions driverOpts;
        if (_useEdge)
        {
            // Windows 无 Chrome 回退 Edge：复用隐身/无头/窗口参数，不设置 BinaryLocation，
            // 交由 Selenium Manager 自动定位系统自带的 Edge 二进制与 msedgedriver
            var edge = new EdgeOptions();
            ConfigureCommonOptions(edge, browserOptions);
            driverOpts = edge;
        }
        else
        {
            driverOpts = options;
        }

        var driver = await Task.Run(() => StealthClient.Instantiate(driverOpts, stealthInstanceSettings));
        driver.Manage().Timeouts().PageLoad = browserOptions.Timeout;
        driver.Manage().Timeouts().AsynchronousJavaScript = browserOptions.Timeout;
        driverPack = new(driver, browserOptions.Timeout + TimeSpan.FromSeconds(5));

        // Bing 首次搜索结果相关性不稳定，浏览器实例创建时先执行一次无关搜索预热。
        await Search("java 漏洞", false);
        return driver;
    }

    /// <summary>
    /// 为 Chrome/Edge 通用选项应用隐身/无头/窗口等参数（两者同属 Chromium 内核，可复用同一套 stealth 逻辑）。
    /// 不含 BinaryLocation：Chrome 由调用方按需设置，Edge 交由 Selenium Manager 自动定位。
    /// </summary>
    private static ChromiumOptions ConfigureCommonOptions(ChromiumOptions options, BrowserOptions bo)
    {
        options.ConfigureForWebScraping();
        if (bo.Headless)
        {
            options.EnableHeadlessMode();
        }
        options.ApplyStealth();
        options.AddArgument($"--window-size={bo.Width},{bo.Height}");
        if (bo.DeviceScaleFactor != 1.0)
        {
            options.AddArgument($"--force-device-scale-factor={bo.DeviceScaleFactor}");
        }
        return options;
    }

    /// <summary>
    /// 检测当前平台是否可用 Chrome。非 Windows 恒返回 true（沿用原 Chrome 行为）。
    /// Windows 上依次检查 CHROME_BIN 环境变量与常见安装路径；任一命中即视为可用。
    /// 注：为保持跨平台编译零额外依赖，未读取注册表（标准安装路径已覆盖绝大多数场景）。
    /// </summary>
    internal static bool IsChromeAvailable()
        => IsChromeAvailable(
            Environment.GetEnvironmentVariable,
            File.Exists,
            ChromeCandidatePaths());

    private static string[] ChromeCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
        ];
    }

    /// <summary>
    /// <see cref="IsChromeAvailable"/> 的可注入重载：环境变量读取、文件存在判定与候选路径均可由
    /// 测试替换，从而在不依赖真实文件系统/注册表的前提下确定性地验证"Windows 无 Chrome → 回退 Edge"的判定逻辑。
    /// </summary>
    internal static bool IsChromeAvailable(
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> fileExists,
        IEnumerable<string> candidatePaths)
    {
        var chromeBin = getEnvironmentVariable("CHROME_BIN");
        if (!string.IsNullOrWhiteSpace(chromeBin) && fileExists(chromeBin))
        {
            return true;
        }

        foreach (var path in candidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && fileExists(path))
            {
                return true;
            }
        }

        return false;
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

    public void Dispose()
    {
        // 先停掉倒计时（防止计时器回调再次 CloseBrowser），再完整清理浏览器进程
        resourceCountdown.Dispose();
        CloseBrowser();
        GC.SuppressFinalize(this);
    }
}
