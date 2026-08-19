

using DataProvider;
using CommonLib;
using MerryBot;
using NapcatClient;
using NLog;

// --- data path ---
string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
string logFileDir = "log";
string dbPath = "plugin_data.db";

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
// 启动配置（setting.toml）：WebUI 监听地址等启动必需项，文件不存在时生成默认模板
StartupConfig.Load(dataPath);

var pluginDb = new PluginStorageDatabase(dbPath);
// 打开数据库后先做 schema 迁移（幂等），再初始化配置，保证新旧键格式一致
await pluginDb.MigrateAsync();
ConfigManager.Initialize(pluginDb).Wait();
//init logger
// 统一 layout：WebUI 日志页的 DetectLevel 正则（\b(TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\b）可解析 |LEVEL| 段
var logLayout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception: |${exception:format=tostring}}";
var nlogConfig = new NLog.Config.LoggingConfiguration();
var coloredConsole = new NLog.Targets.ColoredConsoleTarget("console")
{
    Layout = logLayout,
    UseDefaultRowHighlightingRules = true,
};
nlogConfig.AddTarget(coloredConsole);
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, coloredConsole);
var fileTarget = new NLog.Targets.FileTarget("file")
{
    // 按天命名 bot-2026-08-19.log，便于 WebUI 按 *.log 枚举浏览历史；单日超 10MB 追加序列号归档
    FileName = Path.Combine(logFileDir, "bot-${shortdate}.log"),
    Layout = logLayout,
    ArchiveEvery = NLog.Targets.FileArchivePeriod.Day,
    ArchiveAboveSize = 10 * 1024 * 1024,
    MaxArchiveFiles = 30,
    ArchiveSuffixFormat = ".{####}",
};
nlogConfig.AddTarget(fileTarget);
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
NLog.LogManager.Configuration = nlogConfig;
// 统一日志门面：未显式注入 logger 的库（LlmClient/LlmBackend/HistoryRecorder/WebUI mapper 等）汇入 NLog
SimpleLog.Default = new NLogAdapter("CommonLib");
var currentLogger = LogManager.GetCurrentClassLogger();
currentLogger.Debug("program start");

var config = ConfigManager.Instance;
if (config.AuthorizedUser < 0)
{
    currentLogger.Warn("'authorized-user' is not valid");
}

var logger = new NLogAdapter();
// 构造不再同步等待登录信息（账号信息由 BotClient 后台异步获取），Napcat 未启动也能正常启动进程
var botClient = new BotClient(config.NapcatServer, config.NapcatToken, logger, dataPath);

Logic logic = new Logic(botClient, pluginDb);

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
return 0;
