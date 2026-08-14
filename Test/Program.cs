using BotPlugin;
using BrowserService;
using DataProvider;
using MerryBot;
using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

public static partial class Program
{
    public static async Task Main(string[] args)
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        ConfigManager.SettingFile = Path.Combine(dataPath, "setting.toml");
        ConfigManager.Initialize().Wait();
        await TestMarkdownRender();
    }
    static Browser browser = new Browser(new BrowserOptions() { BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN") });
    static async Task TestWebFetch()
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


    public static async Task TestBrowser()
    {
        Browser browser = new Browser(false);
        var result = await browser.Search("React 最近漏洞 安全漏洞 2025", false);
        Console.WriteLine(result);
    }

    public static async Task TestGitFetchMerge()
    {
        var (diff, messages, hasChanges) = await ViewVersion.GitFetchMerge();
        Console.WriteLine($"Has changes: {hasChanges}");
        Console.WriteLine(diff);
        Console.WriteLine(messages);
    }

    public static void TestToml()
    {
        string tomlContent = @"
id=2
[Client]
server_address = ""192.168.1.1""
port = 80
enabled = false
";

        TomlTable document = TomlSerializer.Deserialize<TomlTable>(tomlContent)!;

        // 从文档中获取值
        var id = document["id"];

        var childNode = (TomlTable)document["Client"];
        var address = childNode["server_address"];
        var port = childNode["port"];
        var enabled = childNode["enabled"];


        Console.WriteLine($"Address: {address}, Port: {port}, Enabled: {enabled}");
    }
    class Data
    {
        public long Value;
    }
    public static async Task TestPluginStorageDatabase()
    {
        string testDbPath = "test_plugin_data.db";
        if (File.Exists(testDbPath))
        {
            File.Delete(testDbPath);
        }
        const string TEST_PLUGIN = "TestPlugin";

        var testData = new Data { Value = 114514 };

        using (var db = new PluginStorageDatabase(testDbPath))
        {
            await db.StorePluginData(TEST_PLUGIN, testData);
            Console.WriteLine("数据已存储");

            Data retrieved = (await db.GetPluginData(TEST_PLUGIN) as Data)!;
            Console.WriteLine($"数据已取出: {retrieved}");

            if (retrieved == null)
            {
                Console.WriteLine("测试失败: 数据为空");
                return;
            }

            var retrievedObj = (dynamic)retrieved;
            if (retrievedObj.Value != 114514)
            {
                Console.WriteLine("测试失败: 数据不匹配");
                return;
            }
            Console.WriteLine("第一次测试通过");
        }

        Console.WriteLine("数据库已关闭");

        using (var db2 = new PluginStorageDatabase(testDbPath))
        {
            Data retrieved2 = (await db2.GetPluginData(TEST_PLUGIN) as Data)!;
            Console.WriteLine($"重新打开后数据: {retrieved2}");

            if (retrieved2 == null)
            {
                Console.WriteLine("测试失败: 重新打开后数据为空");
                return;
            }

            Data retrievedObj2 = (Data)retrieved2;
            if (retrievedObj2.Value != 114514)
            {
                Console.WriteLine("测试失败: 重新打开后数据不匹配");
                return;
            }
            Console.WriteLine("持久化测试通过");
            //modify
            retrievedObj2.Value = 1919810;
            await db2.StorePluginData(TEST_PLUGIN, retrievedObj2);
            if (((Data)(await db2.GetPluginData(TEST_PLUGIN))!).Value != 1919810)
            {
                Console.WriteLine("测试失败: not match");
                return;
            }
            Console.WriteLine("modify测试通过");
        }

        if (File.Exists(testDbPath))
        {
            File.Delete(testDbPath);
        }
        Console.WriteLine("所有测试通过!");
    }
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