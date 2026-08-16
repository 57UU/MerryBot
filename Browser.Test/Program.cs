using BrowserService;
using System.Diagnostics;

// 手工测试台：验证 Browser.View 对指定 URL 是否会触发超时。
// 依赖本机 Chrome（或 CHROME_BIN 环境变量），非 CI 自动化测试。

// 第一个命令行参数为 URL，默认测 flutter_gemma（便于复现原始问题）。
// 例: dotnet run --project Browser.Test -- https://www.baidu.com
string url = args.Length > 0 ? args[0] : "https://pub.dev/packages/flutter_gemma";

// 默认 BrowserOptions.Timeout = 10s，与生产环境一致；
// 透传 CHROME_BIN 以兼容无系统 Chrome 的环境。
string? chromeBin = Environment.GetEnvironmentVariable("CHROME_BIN");

// 先用默认超时（10s）测一次，复现生产行为；
// 再用放宽的超时（30s）测一次，判断是否单纯因页面加载慢导致默认超时。
await RunOnceAsync(TimeSpan.FromSeconds(10), "默认超时 10s");
await RunOnceAsync(TimeSpan.FromSeconds(30), "放宽超时 30s");

async Task RunOnceAsync(TimeSpan timeout, string label)
{
    Console.WriteLine($"==== {label} ====");
    Console.WriteLine($"URL: {url}");
    var options = new BrowserOptions
    {
        BinaryPath = chromeBin,
        Timeout = timeout,
    };

    using Browser browser = new Browser(options);
    var sw = Stopwatch.StartNew();
    try
    {
        string result = await browser.View(url);
        sw.Stop();
        Console.WriteLine($"耗时: {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"结果长度: {result.Length} 字符");
        bool isFailure = result.StartsWith("页面加载失败", StringComparison.Ordinal);
        Console.WriteLine($"状态: {(isFailure ? "失败（可能超时）" : "成功")}");
        if (isFailure)
        {
            Console.WriteLine($"错误信息: {result}");
        }
        else
        {
            // 只打印前 500 字符，避免刷屏
            string preview = result.Length > 500 ? result[..500] + "..." : result;
            Console.WriteLine($"内容预览:\n{preview}");
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"耗时: {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"异常: {ex.GetType().Name}: {ex.Message}");
    }
    Console.WriteLine();
}
