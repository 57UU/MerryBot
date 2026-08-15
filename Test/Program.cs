using BotPlugin;
using BrowserService;
using DataProvider;
using MerryBot;
using System.Reflection;

public static partial class Program
{
    public static async Task Main(string[] args)
    {
        // 手动验证工具（非 CI）：Markdown 渲染依赖本机 Chrome（或 CHROME_BIN 环境变量）
        // 与 CDN 网络（加载 MathJax/Mermaid 资源）；联网/浏览器测试默认不执行。
        Console.WriteLine("Markdown 渲染测试开始（依赖: Chrome + CDN 网络）。");
        try
        {
            string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
            var pluginDb = new PluginStorageDatabase(Path.Combine(dataPath, "plugin_data.db"));
            ConfigManager.Initialize(pluginDb).Wait();
            await TestMarkdownRender();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"测试执行失败: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("提示: 请确认已安装 Chrome（或设置 CHROME_BIN 环境变量），且网络可访问 CDN。");
            Environment.ExitCode = 1;
        }
    }
    static Browser browser = new Browser(new BrowserOptions() { BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN") });
    // Dev-only：需外网与本地 Chrome，Main 不调用；勿在 CI/自动化中执行。
    static async Task TestWebFetchDevOnly()
    {
        var url = "https://scu.edu.cn/zzjg1/yxsz.htm";
        var result=await browser.View(url);
        Console.WriteLine(result);
    }
    public static async Task TestMarkdownRender()
    {
        var md = longLatex;
        using Browser browser = new Browser(new BrowserOptions(){BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN")});

        var img = await browser.TakeMarkdownScreenshot(md);
        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_math_mermaid.png");
        await File.WriteAllBytesAsync(outputPath, img);
        Console.WriteLine($"Markdown 渲染图片已保存至: {outputPath}");
    }


    // Dev-only：需外网与本地 Chrome，Main 不调用；勿在 CI/自动化中执行。
    public static async Task TestBrowserDevOnly()
    {
        Browser browser = new Browser(false);
        var result = await browser.Search("React 最近漏洞 安全漏洞 2025", false);
        Console.WriteLine(result);
    }

    // 注意：禁止在此添加会真实执行 `git fetch` / `git merge` 的测试方法——会修改仓库工作区。

    static T? NullableFunction<T>() where T : struct
    {
        return default;
    }
    static T? NullableFunction2<T>()
    {
        return default;
    }
    static void TestStructNullable()
    {
        var value = NullableFunction<int>(); //int?
        var value2 = NullableFunction2<int>();//int
        Console.WriteLine(value == null);
        Console.WriteLine(value2.GetType());
    }
}
