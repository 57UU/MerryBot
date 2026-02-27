using BotPlugin;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using DataService;
using Microsoft.AspNetCore.Builder;

namespace BotPlugin;

[PluginTag("StorageManager", "管理AiMessageStorage、GroupHistoryRecorder和后台网站的生命周期", priority: 999, type: PluginType.Background)]
public class StorageManagerPlugin : Plugin
{
    private HistoryRecorder historyRecorder;
    private string dbPath;
    private int machineCode;
    WebApplication webApplication;
    
    public HistoryRecorder GroupHistoryRecorder => historyRecorder;
    public HistoryRecorder AiMessageStorage => historyRecorder;
    const string machineCodeKey = "machine-code";

    public StorageManagerPlugin(PluginInterop interop) : base(interop)
    {
        dbPath = Path.Combine(interop.PathPrefix, "group_history.db");
        
        historyRecorder = new HistoryRecorder(dbPath);
        
        Logger.Info($"StorageManagerPlugin 初始化完成，群历史数据库路径: {dbPath}");


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
        webApplication = HistoryWebFrontend.Program.CreateApp(historyRecorder);
        _=webApplication.RunAsync();

    }
    
    
    public override void Dispose()
    {

        historyRecorder?.Dispose();
        webApplication.DisposeAsync().AsTask().Wait();
        Logger.Info("StorageManagerPlugin released");
        base.Dispose();
        GC.SuppressFinalize(this);
    }

}
