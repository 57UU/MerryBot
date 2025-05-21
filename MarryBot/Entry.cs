

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


Logic logic = new Logic(botClient,config.qq_groups);

botClient.OnGroupMessageReceived += logic.OnGroupMessageReceived;

await Utils.WaitForever();
