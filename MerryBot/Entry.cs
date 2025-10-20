

using MerryBot;
using NapcatClient;
using NLog;

string? settingPath = Environment.GetEnvironmentVariable("MR_BOT_SETTING");
if(settingPath != null)
{
    Config.SettingFile= settingPath;
}

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


Logic logic = new Logic(botClient);



await Utils.WaitForever();
