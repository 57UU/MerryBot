using OpenQA.Selenium;
using OpenQA.Selenium.Support.Extensions;
using Markdown2Html;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;

namespace BrowserService;

/// <summary>
/// Browser 的公开操作：页面查看、Markdown/HTML 截图、搜索、微博热搜。
/// 与基础设施（Browser.cs）、纯辅助（Browser.Helpers.cs）同为 partial 拆分。
/// </summary>
public partial class Browser
{
    public int ExecuteScriptDelayTime { set; get; } = 50;
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

        // 整体超时看门狗：Timeout 语义为"整体操作时限"。
        // 各子阶段（PageLoad/等待/脚本执行）超时之和可能远超配置值，且 WebDriver 同步调用
        // 卡死时无兜底会永久挂起；超时后强制关闭浏览器中断卡住的调用，下一次请求会重建。
        var completed = await Task.WhenAny(task, Task.Delay(browserOptions.Timeout));
        if (completed != task)
        {
            try
            {
                CloseBrowser();
            }
            catch
            {
                // 清理失败不掩盖超时结果
            }
            return $"页面加载失败: 页面加载超时（{browserOptions.Timeout.TotalSeconds:F0} 秒）";
        }

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
}
