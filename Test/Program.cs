using BotPlugin;
using DataService;
using MerryBot;
using Microsoft.Data.Sqlite;
using System;
using System.Text.Json;
using ZhipuClient;
using LiteHistoryRecorder = DataService.HistoryRecorder;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        Config.SettingFile = Path.Combine(dataPath, "setting.json");
        Config.Initialize().Wait();

        await MigrateAiMessages();
    }

    public static async Task MigrateAiMessages()
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        string sqliteDbPath = Path.Combine(dataPath, "ai_messages.db");
        string liteDbPath = Path.Combine(dataPath, "group_history.db");

        if (!File.Exists(sqliteDbPath))
        {
            Console.WriteLine("SQLite 数据库文件不存在，跳过迁移");
            return;
        }

        Console.WriteLine("开始迁移 AI 消息数据...");

        using var sqliteConn = new SqliteConnection($"Data Source={sqliteDbPath}");
        sqliteConn.Open();

        string selectSql = "SELECT Id, Group_Id, Message_Type, Content, Time FROM AI_Message_Data_Table";
        using var selectCommand = new SqliteCommand(selectSql, sqliteConn);
        using var reader = await selectCommand.ExecuteReaderAsync();

        var aiMessages = new List<(long Id, long GroupId, string MessageType, string Content, long Time)>();
        while (await reader.ReadAsync())
        {
            aiMessages.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4)
            ));
        }
        reader.Close();

        Console.WriteLine($"从 SQLite 读取了 {aiMessages.Count} 条 AI 消息");

        using var historyRecorder = new LiteHistoryRecorder(liteDbPath);
        int migratedCount = 0;
        foreach (var msg in aiMessages)
        {
            historyRecorder.RecordAiMessage(msg.GroupId, msg.MessageType, msg.Content);
            migratedCount++;
            if (migratedCount % 100 == 0)
            {
                Console.WriteLine($"已迁移 {migratedCount}/{aiMessages.Count} 条...");
            }
        }

        Console.WriteLine($"迁移完成！共迁移 {migratedCount} 条 AI 消息");
        sqliteConn.Close();
        string backupPath = sqliteDbPath + ".bak";
        File.Move(sqliteDbPath, backupPath, true);
        Console.WriteLine($"已将原 SQLite 数据库备份到: {backupPath}");
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