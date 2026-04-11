using OpenQA.Selenium.Chrome;
using System.Text.Json.Serialization;
using System.Timers;

namespace ZhipuClient;

public class SearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;
}

public class ResourceCountdown : IDisposable
{
    // 倒计时时间：5分钟（毫秒）
    private readonly int TimeoutMilliseconds;

    // 计时器对象
    private readonly System.Timers.Timer _timer;

    // 释放资源的回调函数
    private readonly Action _releaseCallback;

    // 资源是否已释放的标志
    public bool IsReleased { get; private set; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="resource">需要管理的资源</param>
    /// <param name="releaseCallback">释放资源的回调函数</param>
    public ResourceCountdown(Action releaseCallback, int timeoutMilliseconds = 5 * 60 * 1000)
    {
        TimeoutMilliseconds = timeoutMilliseconds;
        _releaseCallback = releaseCallback ?? throw new ArgumentNullException(nameof(releaseCallback));
        IsReleased = false;
        // 初始化计时器
        _timer = new(TimeoutMilliseconds);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = false; // 只触发一次，需要手动重置
    }
    /// <summary>
    /// 开始跟踪资源
    /// </summary>
    public void Start()
    {
        // 启动计时器
        _timer.Start();
        IsReleased = false;
    }

    /// <summary>
    /// 使用资源，重置倒计时
    /// </summary>
    public void UseResource()
    {
        if (IsReleased)
        {
            return;
        }

        // 重置计时器
        ResetTimer();
    }

    /// <summary>
    /// 重置倒计时
    /// </summary>
    public void ResetTimer()
    {
        if (IsReleased)
        {
            return;
        }

        // 停止并重新启动计时器，重置倒计时
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// 手动释放资源
    /// </summary>
    public void ReleaseResource()
    {
        if (IsReleased)
        {
            return;
        }

        // 调用释放资源的回调函数
        _releaseCallback();

        // 标记为已释放
        IsReleased = true;
    }

    /// <summary>
    /// 计时器到期时执行的方法
    /// </summary>
    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        ReleaseResource();
    }

    public void Dispose()
    {
        ((IDisposable)_timer).Dispose();
    }
}

/// <summary>
/// ChromeOptions的扩展方法类，用于配置爬虫隐身参数
/// </summary>
public static class ChromeOptionsExtensions
{
    /// <summary>
    /// 配置ChromeOptions以增强爬虫隐身性
    /// </summary>
    /// <param name="options">ChromeOptions实例</param>
    /// <param name="userAgent">自定义User-Agent，默认使用Edge浏览器UA</param>
    /// <returns>配置后的ChromeOptions实例</returns>
    public static ChromeOptions ConfigureForWebScraping(this ChromeOptions options, string? userAgent = null)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // 设置User-Agent
        if (!string.IsNullOrEmpty(userAgent))
        {
            options.AddArgument($"user-agent={userAgent}");
        }
        else
        {
            // 默认使用较新的Edge浏览器User-Agent
            options.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0");
        }


        // 增强爬虫隐身性的核心参数
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddExcludedArgument("enable-automation");

        // 性能和稳定性优化
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        // 隐私和隐身模式
        options.AddArgument("--incognito");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--disable-plugins-discovery");

        // 减少干扰
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");

        // 安全相关设置
        options.AddArgument("--ignore-certificate-errors");
        options.AddArgument("--disable-web-security");
        options.AddArgument("--disable-site-isolation-trials");
        options.AddArgument("--disable-features=site-per-process");

        return options;
    }

    /// <summary>
    /// 启用Headless模式的扩展方法
    /// </summary>
    /// <param name="options">ChromeOptions实例</param>
    /// <returns>配置后的ChromeOptions实例</returns>
    public static ChromeOptions EnableHeadlessMode(this ChromeOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.AddArgument("--headless=new");
        options.AddArgument("--disable-gpu");

        return options;
    }
}
