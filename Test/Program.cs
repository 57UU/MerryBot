using BotPlugin;
using MarryBot;
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