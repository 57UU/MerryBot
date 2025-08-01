using MarryBot;
using System;
using System.Text.Json;
using ZhipiAi;
using ZhipuClient;

Browser browser = new Browser();
var t1=browser.view("https://www.baidu.com");
Task.WaitAll(new Task[] { t1 });
Console.WriteLine(t1.Result);

Config.Initialize().Wait();
var config=Config.instance;
string token = ((JsonElement)config.Variables["ai-token"]).GetString();
string prompt = ((JsonElement)config.Variables["ai-prompt"]).GetString();
ZhipuAi zhipu = new ZhipuAi(token, prompt);
while (true)
{
    Console.Write("User: ");
    await foreach (var i in zhipu.Ask(Console.ReadLine(), 114514, "default"))
    {
        Console.WriteLine(i);
    }
}