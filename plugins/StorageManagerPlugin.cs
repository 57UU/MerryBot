using BotPlugin;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using DataService;

namespace BotPlugin;

[PluginTag("StorageManager", "管理AiMessageStorage、GroupHistoryRecorder和后台网站的生命周期", priority: 999, type: PluginType.Background)]
public class StorageManagerPlugin : Plugin
{
    private HistoryRecorder historyRecorder;
    private AiMessageRecorder aiMessageStorage;
    private string dbPath;
    private string aiDbPath;
    private int machineCode;
    
    public HistoryRecorder GroupHistoryRecorder => historyRecorder;
    public AiMessageRecorder AiMessageStorage => aiMessageStorage;
    const string machineCodeKey = "machine-code";

    public StorageManagerPlugin(PluginInterop interop) : base(interop)
    {
        dbPath = Path.Combine(interop.PathPrefix, "group_history.db");
        aiDbPath = Path.Combine(interop.PathPrefix, "ai_messages.db");
        
        historyRecorder = new HistoryRecorder(dbPath);
        aiMessageStorage = new AiMessageRecorder(aiDbPath);
        
        Logger.Info($"StorageManagerPlugin 初始化完成，群历史数据库路径: {dbPath}");
        Logger.Info($"StorageManagerPlugin 初始化完成，AI消息数据库路径: {aiDbPath}");


        var _machineCode=interop.GetJsonElement(machineCodeKey);
        if (_machineCode == null)
        {
            //gen
            machineCode = (int)(new Random().NextSingle() * 32);
            interop.SetVarible(machineCodeKey,machineCode);
            interop.SaveConfig().Wait();
        }
        else
        {
            machineCode = _machineCode.Value.GetInt32();
        }

    }
    
    
    public override void Dispose()
    {

        historyRecorder?.Dispose();
        aiMessageStorage?.Dispose();
        Logger.Info("StorageManagerPlugin 已释放所有资源");
        base.Dispose();
        GC.SuppressFinalize(this);
    }

}
