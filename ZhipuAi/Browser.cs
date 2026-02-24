using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.Support.Extensions;
using OpenQA.Selenium.Support.UI;
using SeleniumStealth.NET.Clients;
using SeleniumStealth.NET.Clients.Enums;
using SeleniumStealth.NET.Clients.Extensions;
using SeleniumStealth.NET.Clients.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using static System.Net.Mime.MediaTypeNames;

namespace ZhipuClient;

class DriverPack
{
    public ChromeDriver driver;
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
    ChromeDriver? driver { get { return driverPack?.driver; } }
    ChromeOptions options = new();
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    string getSearchResult = null;
    string jsReader = null, preprocessWbHot = null, preprocessBingResult = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    SemaphoreSlim mutex = new(1);
    private static Task<string> LoadScript(string fileName)
    {
        if (!fileName.EndsWith(".js"))
        {
            fileName += ".js";
        }
        return File.ReadAllTextAsync("./javascript/" + fileName, Encoding.UTF8);
    }
    private async Task LoadScripts()
    {
        string[] scriptFiles = [
            "readWeb",
            "getSearchResult2",
            "preprocessWbHot",
            "preprocessBingResult"
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
    }
    readonly StealthInstanceSettings stealthInstanceSettings = new();
    readonly ResourceCountdown resourceCountdown;
    public Browser(bool headless = true)
    {
        resourceCountdown = new(CloseBrowser);

        options.ConfigureForWebScraping();

        if (headless)
        {
            options.EnableHeadlessMode();
        }

        options.ApplyStealth();

        bool isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (isLinuxArm64)
        {
            Console.WriteLine("Arch: Linux Arm64;you may need to install chromedriver manually");
            stealthInstanceSettings.ChromeDriverPath = "/usr/bin/chromedriver";
        }


        LoadScripts().Wait();
    }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    private async Task<ChromeDriver> LoadBrowser()
    {
        resourceCountdown.Start();
        var driver = await Task.Run(() => Stealth.Instantiate(options, stealthInstanceSettings));
        driver.Manage().Timeouts().PageLoad = Timeout;
        driver.Manage().Timeouts().AsynchronousJavaScript = Timeout;
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
            _=GotoBlankPage();
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            return $"调用失败 {t.Exception}";
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
            _=GotoBlankPage();
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
            _=GotoBlankPage();
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

