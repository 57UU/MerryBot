using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace ZhipiAi;

public class Browser
{
    IWebDriver driver;
    OpenQA.Selenium.Chrome.ChromeOptions options = new();
    string jsReader,getSearchResult;
    SemaphoreSlim mutex = new(1);
    public Browser()
    {
        options.AddArgument("--headless");
        driver = new OpenQA.Selenium.Chrome.ChromeDriver(options);
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
        
        var task= Task.Run(async () =>
        {
            mutex.Wait();
            driver.Navigate().GoToUrl(ToStandardUri(url));
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
    public Task<string> Search(string keyword)
    {

        var task = Task.Run(async () =>
        {
            mutex.Wait();
            driver.Navigate().GoToUrl(ToStandardUri($"https://cn.bing.com/search?q={keyword}"));
            await Task.Delay(100);
            IJavaScriptExecutor executor = (IJavaScriptExecutor)driver;
            var result = executor.ExecuteScript(getSearchResult).ToString();
            
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
