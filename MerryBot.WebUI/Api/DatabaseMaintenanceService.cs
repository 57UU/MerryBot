using CommonLib;
using DataProvider;
using DataService;

namespace MerryBot.WebUI.Api;

/// <summary>数据库大小查询结果。</summary>
public sealed record DatabaseSizesDto(
    long PluginDbBytes,
    string PluginDbSize,
    long HistoryDbBytes,
    string HistoryDbSize);

/// <summary>单库 Rebuild 结果。</summary>
public sealed record RebuildResultDto(
    string Target,
    long BeforeBytes,
    string BeforeSize,
    long AfterBytes,
    string AfterSize,
    long ReducedBytes,
    string ReducedSize);

/// <summary>数据库维护直连服务：大小查询与 Rebuild（碎片整理/压缩），逻辑与 DatabaseApiMapper 同体，供 Blazor 页面直接调用。</summary>
public static class DatabaseMaintenanceService
{
    private static readonly SemaphoreSlim RebuildLock = new(1, 1);

    public static DatabaseSizesDto GetSizes(PluginStorageDatabase pluginDb, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(pluginDb);
        ArgumentNullException.ThrowIfNull(historyRecorder);
        long pluginBytes = pluginDb.GetDatabaseFileSize();
        long historyBytes = historyRecorder.GetDatabaseFileSize();
        return new DatabaseSizesDto(
            PluginDbBytes: pluginBytes,
            PluginDbSize: Format.FormatFileSize(pluginBytes),
            HistoryDbBytes: historyBytes,
            HistoryDbSize: Format.FormatFileSize(historyBytes));
    }

    public static async Task<IReadOnlyList<RebuildResultDto>> RebuildAsync(
        PluginStorageDatabase pluginDb, HistoryRecorder historyRecorder, string? target)
    {
        ArgumentNullException.ThrowIfNull(pluginDb);
        ArgumentNullException.ThrowIfNull(historyRecorder);
        string normalized = (target ?? "all").Trim().ToLowerInvariant();
        if (normalized is not ("plugin" or "history" or "all"))
        {
            throw new ArgumentException("target 仅支持 plugin / history / all");
        }

        if (!await RebuildLock.WaitAsync(0))
        {
            throw new InvalidOperationException("已有 Rebuild 任务正在执行，请稍后再试。");
        }

        try
        {
            List<RebuildResultDto> results = [];

            if (normalized is "plugin" or "all")
            {
                long before = pluginDb.GetDatabaseFileSize();
                await pluginDb.RebuildAsync();
                long after = pluginDb.GetDatabaseFileSize();
                results.Add(BuildResult("plugin", before, after));
            }

            if (normalized is "history" or "all")
            {
                long before = historyRecorder.GetDatabaseFileSize();
                await historyRecorder.RebuildAsync();
                long after = historyRecorder.GetDatabaseFileSize();
                results.Add(BuildResult("history", before, after));
            }

            return results;
        }
        catch (Exception ex) when (ex is not (ArgumentException or InvalidOperationException))
        {
            string baseMsg = ex.GetBaseException().Message ?? ex.Message;
            bool isLoop = baseMsg.Contains("loop", StringComparison.OrdinalIgnoreCase)
                || baseMsg.Contains("Detected loop", StringComparison.OrdinalIgnoreCase);
            if (isLoop)
            {
                throw new InvalidOperationException(
                    $"Rebuild 失败：检测到索引损坏（{baseMsg}）。已尝试容错重建仍失败。建议：1) 立即备份 " +
                    $"plugin_data.db({pluginDb.GetDatabaseSize()}), group_history.db({historyRecorder.GetDatabaseSize()}) " +
                    $"2) 停止 Bot 后用 LiteDB Studio 打开文件并尝试修复，或删除重建该库（历史库可删除 group_history.db 后重启自动重建，插件库删除会丢失配置/记忆）。详细日志见服务端。", ex);
            }
            throw new InvalidOperationException($"Rebuild 失败: {baseMsg}", ex);
        }
        finally { RebuildLock.Release(); }
    }

    private static RebuildResultDto BuildResult(string target, long before, long after)
    {
        long reduced = before - after;
        return new RebuildResultDto(
            Target: target,
            BeforeBytes: before,
            BeforeSize: Format.FormatFileSize(before),
            AfterBytes: after,
            AfterSize: Format.FormatFileSize(after),
            ReducedBytes: reduced,
            ReducedSize: Format.FormatFileSize(Math.Abs(reduced)) + (reduced >= 0 ? " (已释放)" : " (增大)"));
    }
}
