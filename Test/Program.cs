using BotPlugin;
using MarryBot;
using System;
using System.Text.Json;
using ZhipuClient;


//Browser browser = new();
//var re = await browser.Search("apple", false);
//Console.WriteLine(re);

Config.Initialize().Wait();
var config=Config.instance;
var model = ModelPreset.DeepSeekChat;
var token_key = model.ApiTokenDictKey;
string token = ((JsonElement)config.Variables[token_key]).GetString();
string prompt = ((JsonElement)config.Variables["ai-prompt"]).GetString();
ZhipuAi zhipu = new ZhipuAi(token, prompt, model);
while (true)
{
    Console.Write("User: ");
    await foreach (var i in zhipu.Ask(Console.ReadLine(), 114514, "default"))
    {
        Console.WriteLine(i);
    }
}