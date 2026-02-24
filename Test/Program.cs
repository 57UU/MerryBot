using BotPlugin;
using MerryBot;
using System;
using System.Text.Json;
using ZhipuClient;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        Config.SettingFile = Path.Combine(dataPath, "setting.json");
        Config.Initialize().Wait();

        await TestZhipuAi();
    }

    public static async Task TestZhipuAi()
    {
        var config = Config.Instance;

        var model = ModelPreset.Glm_4_7_Flash_Free;
        var token_key = model.ApiTokenDictKey;
        string token = ((JsonElement)config.Variables[token_key]).GetString()!;
        string prompt = ((JsonElement)config.Variables["ai-prompt"]).GetString()!;
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
        Terminal terminal = new Terminal();
        while (true)
        {
            Console.Write("User: ");
            string input = Console.ReadLine();
            if (input == "exit")
            {
                break;
            }
            var result = await terminal.RunCommandAutoTimeoutAsync(input);
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
        string? token = Config.Instance.Variables[model.ApiTokenDictKey].GetString()!;
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
}