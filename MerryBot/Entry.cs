

using MerryBot;
using NapcatClient;
using NLog;

// --- data path ---
string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
string logFileDir = "log";
string dbPath = "plugin_data.db";

ConfigManager.SettingFile = Path.Combine(dataPath, "setting.toml");
logFileDir = Path.Combine(dataPath, logFileDir);
dbPath = Path.Combine(dataPath, dbPath);

if (Utils.CreateDirectory(dataPath))
{
    Console.WriteLine($"data directory created:{dataPath}");
}
if (Utils.CreateDirectory(logFileDir))
{
    Console.WriteLine($"log directory created:{logFileDir}");
}
var logFilePath = Path.Combine(logFileDir, Utils.GenerateFileNameByCurrentTime());

ConfigManager.Initialize().Wait();
//init logger
var nlogConfig = new NLog.Config.LoggingConfiguration();
var coloredConsole = new NLog.Targets.ColoredConsoleTarget("console")
{
    Layout = "${time:format=HH\\:mm\\:ss} ${level:uppercase=true:padding=-5} ${message}${onexception: ${exception:format=tostring}}",
    UseDefaultRowHighlightingRules = true,
};
nlogConfig.AddTarget(coloredConsole);
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, coloredConsole);
var fileTarget = new NLog.Targets.FileTarget("file")
{
    FileName = $"{logFilePath}.log",
};
nlogConfig.AddTarget(fileTarget);
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
NLog.LogManager.Configuration = nlogConfig;
var currentLogger = LogManager.GetCurrentClassLogger();
currentLogger.Debug("program start");

var config = ConfigManager.Instance;
if (config.AuthorizedUser < 0)
{
    currentLogger.Warn("'authorized-user' is not valid");
}

var logger = new NLogAdapter();
var botClient = new BotClient(config.NapcatServer, config.NapcatToken, logger, dataPath);


Logic logic = new Logic(botClient, dbPath);

// 使用 CancellationTokenSource 来控制程序生命周期
using var cts = new CancellationTokenSource();

// 处理 Ctrl+C 信号，优雅地关闭程序
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true; // 防止进程立即终止
    currentLogger.Info("Shutdown signal received, closing...");
    cts.Cancel();
};

await Utils.WaitForShutdownAsync(cts.Token);

currentLogger.Info("Application is shutting down...");
logic.Shutdown();
