using BotPlugin;
using MarryBot;
using System;
using System.Text.Json;
using ZhipuClient;


RateLimiter rateLimiter = new(3,1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
rateLimiter.Increase(1);
Console.WriteLine(rateLimiter.CheckIsLimited(1));
await Task.Delay(2500);
Console.WriteLine(rateLimiter.CheckIsLimited(1));



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