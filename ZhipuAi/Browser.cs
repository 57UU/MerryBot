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
using static System.Net.Mime.MediaTypeNames;

namespace ZhipuClient;

/// <summary>
/// access web pages with headless chrome
/// </summary>
public class Browser
{
    IWebDriver driver;
    OpenQA.Selenium.Chrome.ChromeOptions options = new();
    [Obsolete]
    string getSearchResult;
    string jsReader, preprocessWbHot,preprocessBingResult;
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
    public Browser()
    {
        options.AddArgument("--headless");
        options.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0");
        //options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1920,1080");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.ApplyStealth();

        bool isLinuxArm64 = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        StealthInstanceSettings stealthInstanceSettings = new();
        if (isLinuxArm64)
        {
            Console.WriteLine("Arch: Linux Arm64");
            stealthInstanceSettings.ChromeDriverPath = "/usr/bin/chromedriver";
        }

        driver = Stealth.Instantiate(options, stealthInstanceSettings);
        LoadScripts().Wait();
        
    }
    static string trim(string s)
    {
        s = s.Replace("\n", "").Replace("\r", "");
        return Regex.Replace(s, @"\s{2,}", " ");
    }
    /// <summary>
    /// view web page
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public Task<string> view(string url)
    {
        return view(ToStandardUri(url));
    }
    /// <summary>
    /// view web page
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public Task<string> view(Uri url)
    {
        
        var task= Task.Run(async () =>
        {
            mutex.Wait();
            driver.Navigate().GoToUrl(url);
            await Task.Delay(100);
            IJavaScriptExecutor executor = (IJavaScriptExecutor)driver;
            var result= executor.ExecuteScript(jsReader).ToString();
            return trim(result);
        });

        return task.ContinueWith((t) => {
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
        var url = ToStandardUri($"https://cn.bing.com/search?q={keyword}" +
            (internationalVersion ? "&ensearch=1" : string.Empty));
        var task = Task.Run(async () =>
        {
            mutex.Wait();
            driver.Navigate().GoToUrl(url);
            await Task.Delay(100);
            IJavaScriptExecutor executor = (IJavaScriptExecutor)driver;
            var result = executor.ExecuteScript(getSearchResult).ToString();
            
            return trim(result);
        });

        return await await task.ContinueWith(async(t) => {
            mutex.Release();
            if (t.Status == TaskStatus.RanToCompletion)
            {
                return t.Result;
            }
            //if the script failed, try to view the page
            return await view(url);
        });
    }
    public Task<string> GetWeiboHot()
    {
        var url = "https://m.weibo.cn/p/106003type=25&filter_type=realtimehot";
        var query = "return document.querySelector(\"#app > div:nth-child(1) > div:nth-child(2) > div:nth-child(3) > div > div\")";
        var delayTimeout = 1500;
        var checkInterval =400;
        var task = Task.Run(async () =>
        {
            mutex.Wait();
            driver.Navigate().GoToUrl(url);
            await Task.Delay(100);
            IJavaScriptExecutor executor = (IJavaScriptExecutor)driver;
            int delay = 0;
            while (true)
            {
                if (executor.ExecuteScript(query) == null)
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
            executor.ExecuteScript(preprocessWbHot);
            var result = executor.ExecuteScript(jsReader).ToString();
            return "|事件|热度|\n"+trim(result);
        });

        return task.ContinueWith((t) => {
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
}
