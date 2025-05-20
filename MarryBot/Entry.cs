

using MarryBot;
using NapcatClient;
using NLog;

Config.Initialize().Wait();
//init logger
var fileName = Utils.GenerateFileNameByCurrentTime();
NLog.LogManager.Setup().LoadConfiguration(builder =>
{
    builder.ForLogger().FilterMinLevel(LogLevel.Debug).WriteToConsole();
    builder.ForLogger().FilterMinLevel(LogLevel.Debug).WriteToFile(fileName: $"{fileName}.log");
});
LogManager.GetCurrentClassLogger().Debug("program start");

var config = Config.instance;

var botClient = new BotClient(config.napcat_server, config.napcat_token);
botClient.Logger = new NLogAdapter();

string introduce = "我是MarryBot，很高兴遇到你";


botClient.OnGroupMessageReceived += async (groupId, chain, data) =>
{
    bool isTargeted = false;
    long selfId = BotUtils.GetSelfId(data);
    if (chain[0].MessageType == "at")
    {
        string target = chain[0].Data["qq"];
        if (target == selfId.ToString())
        {
            isTargeted = true;
        }
    }

    if (isTargeted)
    {
        // at消息
        if (chain.Count >= 2 && chain[1].MessageType == "text")
        {
            string text = chain[1].Data["text"];
            text = text.Trim();
            if (text.StartsWith("/help"))
            {
                botClient.Actions.SendGroupMessage(groupId, introduce);
            }
        }
    }
    else
    {

    }


};

await Utils.WaitForever();
