

using MerryBot;
using NapcatClient;
using NLog;

string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT")??"data";
if (!Directory.Exists(dataPath))
{
    Console.WriteLine("data directory created");
}
string logFileDir = "log";
string dbPath = "plugin_data.db";

Config.SettingFile = Path.Combine(dataPath, "setting.json");
logFileDir = Path.Combine(dataPath, logFileDir);
dbPath=Path.Combine(dataPath, dbPath);

if (!Directory.Exists(logFileDir))
{
    Console.WriteLine("log directory created");
}
var logFilePath=Path.Combine(logFileDir, Utils.GenerateFileNameByCurrentTime());

Config.Initialize().Wait();
//init logger
NLog.LogManager.Setup().LoadConfiguration(builder =>
{
    builder.ForLogger().FilterMinLevel(LogLevel.Debug).WriteToConsole();
    builder.ForLogger().FilterMinLevel(LogLevel.Debug).WriteToFile(fileName: $"{logFilePath}.log");
});
var currentLogger= LogManager.GetCurrentClassLogger();
currentLogger.Debug("program start");

var config = Config.Instance;
if (config.AuthorizedUser < 0)
{
    currentLogger.Warn("'authorized-user' is not valid");
}

var botClient = new BotClient(config.napcat_server, config.napcat_token);
botClient.Logger = new NLogAdapter();


Logic logic = new Logic(botClient, dbPath);



await Utils.WaitForever();
