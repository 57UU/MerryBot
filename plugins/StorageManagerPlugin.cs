using DataService;
using Microsoft.AspNetCore.Builder;

namespace BotPlugin;

[PluginTag("storage-manager", "StorageManager", "管理AiMessageStorage、GroupHistoryRecorder和后台网站的生命周期", priority: 999, type: PluginType.Background)]
public class StorageManagerPlugin : Plugin
{
    private HistoryRecorder historyRecorder;
    private string dbPath;
    private string storagePath;
    private int machineCode;
    WebApplication webApplication;

    public HistoryRecorder GroupHistoryRecorder => historyRecorder;
    public HistoryRecorder AiMessageStorage => historyRecorder;
    const string machineCodeKey = "machine-code";

    public StorageManagerPlugin(PluginInterop interop) : base(interop)
    {
        dbPath = Path.Combine(interop.PathPrefix, "group_history.db");
        storagePath = Path.Combine(interop.PathPrefix, "storage");

        historyRecorder = new HistoryRecorder(dbPath, storagePath);

        Logger.Info($"StorageManagerPlugin 初始化完成，群历史数据库路径: {dbPath}, 存储路径: {storagePath}");


        var _machineCode = interop.GetStructVariable<int>(machineCodeKey);
        if (_machineCode == null)
        {
            //gen
            machineCode = (int)(new Random().NextSingle() * 32);
            interop.SetVarible(machineCodeKey, machineCode);
            interop.SaveConfig().Wait();
        }
        else
        {
            machineCode = _machineCode.Value;
        }
        webApplication = HistoryWebFrontend.Program.CreateApp(historyRecorder);
        _ = webApplication.RunAsync();

    }

    public async Task<string> GetContext(long groupId,int count=5)
    {
        var messages = await historyRecorder.GetMessagesByGroupIdAsync(groupId, count);
        messages.Reverse();
        var context = string.Join("\n", messages.Select(m =>
        {
            var timeStr = m.Time.ToString("yyyy-MM-dd HH:mm");
            var name = string.IsNullOrEmpty(m.SenderGroupNickname) ? m.SenderNickname : m.SenderGroupNickname;
            var content = string.Join("", m.Messages.Select(tm => tm.ToString()));
            return $"[{timeStr}] {name}: {content}";
        }));
        return context;
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
