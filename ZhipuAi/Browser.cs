using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.Support.Extensions;
using SeleniumStealth.NET.Clients;
using SeleniumStealth.NET.Clients.Enums;
using SeleniumStealth.NET.Clients.Extensions;
using SeleniumStealth.NET.Clients.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using static System.Net.Mime.MediaTypeNames;

namespace ZhipuClient;

/// <summary>
/// access web pages with headless chrome
/// </summary>
public partial class Browser
{
    ChromeDriver? driver=null;
    ChromeOptions options = new();
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    string getSearchResult=null;
    string jsReader = null, preprocessWbHot = null, preprocessBingResult = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    SemaphoreSlim mutex = new(1);
    private static Task<string> LoadScript(string fileName)
    {
        if (!fileName.EndsWith(".js"))
        {
            fileName += ".js";
        }
        return File.ReadAllTextAsync("./javascript/"+fileName, Encoding.UTF8);
    }
    private async Task LoadScripts()
    {
        string[] scriptFiles = [
            "readWeb", 
            "getSearchResult",
            "preprocessWbHot",
            "preprocessBingResult"
            ];
        List<Task<string>> tasks= new();
        foreach (var file in scriptFiles)
        {
            tasks.Add(LoadScript(file));
        }
        await Task.WhenAll(tasks);
        jsReader = tasks[0].Result;
        getSearchResult = tasks[1].Result;
        preprocessWbHot = tasks[2].Result;
        preprocessBingResult = tasks[3].Result;
    }
    readonly StealthInstanceSettings stealthInstanceSettings = new();
    readonly ResourceCountdown resourceCountdown ;
    public Browser()
    {
        resourceCountdown = new(CloseBrowser);
        options.AddArgument("--headless");
        options.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0");
        //options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1920,1080");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.ApplyStealth();

        bool isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (isLinuxArm64)
        {
            Console.WriteLine("Arch: Linux Arm64;you may need to install chromedriver manually");
            stealthInstanceSettings.ChromeDriverPath = "/usr/bin/chromedriver";
        }

        
        LoadScripts().Wait(); 
    }
    private Task<ChromeDriver> LoadBrowser()
    {
        resourceCountdown.Start();
        return Task.Run(() => driver = Stealth.Instantiate(options, stealthInstanceSettings));
    }
    private void CloseBrowser()
    {
        if (driver == null)
        {
            return; 
        }
        driver.Quit();
        driver.Dispose();
        driver = null;
    }
    private async Task UseBrowser()
    {
        if (driver == null)
        {
            await LoadBrowser();
        }
        resourceCountdown.UseResource();
    }
    private void GotoBlankPage()
    {
        driver?.Navigate().GoToUrl("about:blank");
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
        var task= Task.Run(async () =>
        {
            mutex.Wait();
            driver!.Navigate().GoToUrl(url);
            await Task.Delay(100);
            var result= driver.ExecuteScript(jsReader)!.ToString()!;
            return Trim(result);
        });

        return await task.ContinueWith((t) => {
            GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            return $"调用失败 {t.Exception}";
        });
    }
    /// <summary>
    /// bing search
    /// </summary>
    /// <param name="keyword"></param>
    /// <returns>search reasult</returns>
    public async Task<string> Search(string keyword,bool internationalVersion)
    {
        await UseBrowser();
        var url = ToStandardUri($"https://cn.bing.com/search?q={HttpUtility.UrlEncode(keyword)}" +
            (internationalVersion ? "&ensearch=1" : string.Empty));
        var task = Task.Run(async () =>
        {
            mutex.Wait();
            driver!.Navigate().GoToUrl(url);
            await Task.Delay(100);
            var result = driver.ExecuteScript(getSearchResult)!.ToString()!;
            
            return Trim(result);
        });

        return await await task.ContinueWith(async(t) => {
            GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            //if the script failed, try to view the page
            return await View(url);
        });
    }
    public async Task<string> GetWeiboHot()
    {
        await UseBrowser();
        var url = "https://m.weibo.cn/p/106003type=25&filter_type=realtimehot";
        var query = "return document.querySelector(\"#app > div:nth-child(1) > div:nth-child(2) > div:nth-child(3) > div > div\")";
        var delayTimeout = 1500;
        var checkInterval =400;
        var task = Task.Run(async () =>
        {
            mutex.Wait();
            driver!.Navigate().GoToUrl(url);
            await Task.Delay(100);
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
            return "|事件|热度|\n"+Trim(result);
        });

        return await task.ContinueWith((t) => {
            GotoBlankPage();
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
}


public class ResourceCountdown:IDisposable
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
        TimeoutMilliseconds=timeoutMilliseconds;
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
