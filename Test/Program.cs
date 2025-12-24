using BotPlugin;
using MerryBot;
using System;
using System.Text.Json;
using ZhipuClient;


//test shell
//Terminal terminal=new();
//while (true)
//{
//    Console.Write("User: ");
//    string input = Console.ReadLine();
//    if (input == "exit")
//    {
//        break;
//    }
//    var result=await terminal.RunCommandAutoTimeoutAsync(input);
//    Console.WriteLine($"out:{result}");
//}
//Browser browser = new Browser(false);
//var result = await browser.Search("React 最近漏洞 安全漏洞 2025", false);

//var (a,b)=await ViewVersion.GitFetchMerge();
//Console.WriteLine(a);
//Console.WriteLine(b);

string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";

Config.SettingFile = Path.Combine(dataPath, "setting.json");

Config.Initialize().Wait();
var config=Config.Instance;
var model = ModelPreset.XiaomiMimoV2;
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