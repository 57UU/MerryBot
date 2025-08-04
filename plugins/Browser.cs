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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace ZhipuClient;

public class Browser
{
    IWebDriver driver;
    OpenQA.Selenium.Chrome.ChromeOptions options = new();
    string jsReader,getSearchResult;
    SemaphoreSlim mutex = new(1);
    public Browser()
    {
        options.AddArgument("--headless");
        options.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0");
        //options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1920,1080");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.ApplyStealth();

        driver = Stealth.Instantiate(options);

        jsReader = File.ReadAllText("readWeb.js",Encoding.UTF8);
        getSearchResult = File.ReadAllText("getSearchResult.js",Encoding.UTF8);
    }
    static string trim(string s)
    {
        s = s.Replace("\n", "").Replace("\r", "");
        return Regex.Replace(s, @"\s{2,}", " ");
    }
    public Task<string> view(string url)
    {
        return view(ToStandardUri(url));
    }
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
    public async Task<string> Search(string keyword)
    {
        var url = ToStandardUri($"https://cn.bing.com/search?q={keyword}");
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
