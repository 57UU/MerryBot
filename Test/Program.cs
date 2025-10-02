using BotPlugin;
using MarryBot;
using System;
using System.Text.Json;
using ZhipuClient;


Browser browser = new();
var re = await browser.Search("apple", false);
Console.WriteLine(re);

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