using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZhipiAi;

public class Browser
{
    IWebDriver driver;
    OpenQA.Selenium.Firefox.FirefoxOptions options = new();
    string jsReader;
    SemaphoreSlim mutex = new(1);
    public Browser()
    {
        options.AddArgument("--headless");
        driver = new OpenQA.Selenium.Firefox.FirefoxDriver(options);
        jsReader = File.ReadAllText("readWeb.js",Encoding.UTF8);
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
            mutex.Release();
            return result.Replace(" ","").Replace("\n","").Replace("\r","");
        });
        
        return task;
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
