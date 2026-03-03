using BotPlugin;
using DataProvider;
using MerryBot;
using System.Reflection;
using System.Text.Json;
using ZhipuClient;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        Config.SettingFile = Path.Combine(dataPath, "setting.json");
        Config.Initialize().Wait();
        await TestPluginStorageDatabase();
    }


    public static async Task TestZhipuAi()
    {
        var config = Config.Instance;

        var model = ModelPreset.MiniMax2_5;
        var token_key = model.ApiTokenDictKey;

        PluginTag tag = typeof(AiMessage).GetCustomAttribute<PluginTag>()!;

        var aiVars = config.Variables[tag.Id];
        string token = aiVars[token_key].GetString()!;
        string prompt = aiVars["ai-prompt"].GetString()!;
        ZhipuAi zhipu = new ZhipuAi(token, prompt, model);
        while (true)
        {
            Console.Write("User: ");
            await foreach (var i in zhipu.Ask(Console.ReadLine()!, 114514, "default"))
            {
                Console.WriteLine(i);
            }
        }
    }

    public static async Task TestTerminal()
    {
        Terminal terminal = Terminal.CreateUserTerminal();
        while (true)
        {
            Console.Write("User: ");
            string input = Console.ReadLine()!;
            if (input == "exit")
            {
                break;
            }
            var result = await terminal.RunCommandAsync(input);
            Console.WriteLine($"out:{result}");
        }
    }

    public static async Task TestBrowser()
    {
        Browser browser = new Browser(false);
        var result = await browser.Search("React 最近漏洞 安全漏洞 2025", false);
        Console.WriteLine(result);
    }

    public static async Task TestGitFetchMerge()
    {
        var (a, b) = await ViewVersion.GitFetchMerge();
        Console.WriteLine(a);
        Console.WriteLine(b);
    }

    public static async Task TestImagePainterDashscope()
    {
        var model = DashscopeModelPreset.QwenImageMax;
        // 使用 "ai-message" 插件的命名空间
        var aiVars = Config.Instance.Variables["ai-message"];
        string? token = aiVars.TryGetValue(model.ApiTokenDictKey, out var tokenElem) ? tokenElem.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("请设置环境变量 DASHSCOPE_API_KEY");
            return;
        }


        var painter = new ImagePainterDashscope(model, token);

        string prompt = "一副典雅庄重的对联悬挂于厅堂之中，房间是个安静古典的中式布置，桌子上放着一些青花瓷，对联上左书\"义本生知人机同道善思新\"，右书\"通云赋智乾坤启数高志远\"， 横批\"智启千问\"，字体飘逸，在中间挂着一幅中国风的画作，内容是岳阳楼。";
        string negativePrompt = "低分辨率，低画质，肢体畸形，手指畸形，画面过饱和，蜡像感，人脸无细节，过度光滑，画面具有AI感。构图混乱。文字模糊，扭曲。";

        Console.WriteLine("开始生成图片...");
        string imageUrl = await painter.DrawImage(prompt, negativePrompt, 1664, 928);
        Console.WriteLine($"图片生成成功: {imageUrl}");
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
}