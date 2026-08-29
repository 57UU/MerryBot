using CommonLib;
using DataProvider;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>数据库大小查询与 Rebuild（碎片整理/压缩）API。</summary>
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

/// <summary>Rebuild 请求体：target 可选 plugin/history/all，默认 all。</summary>
public sealed record RebuildRequest(string? Target);

public static class DatabaseApiMapper
{
    private static readonly SemaphoreSlim RebuildLock = new(1, 1);

    public static void Map(WebApplication app, PluginStorageDatabase pluginDb, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(pluginDb);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var group = app.MapGroup("/api/database");

        group.MapGet("/sizes", () =>
        {
            long pluginBytes = pluginDb.GetDatabaseFileSize();
            long historyBytes = historyRecorder.GetDatabaseFileSize();
            return Results.Ok(new DatabaseSizesDto(
                PluginDbBytes: pluginBytes,
                PluginDbSize: Format.FormatFileSize(pluginBytes),
                HistoryDbBytes: historyBytes,
                HistoryDbSize: Format.FormatFileSize(historyBytes)));
        });

        group.MapPost("/rebuild", async (RebuildRequest? body) =>
        {
            string target = (body?.Target ?? "all").Trim().ToLowerInvariant();
            if (target is not ("plugin" or "history" or "all"))
            {
                return Results.BadRequest("target 仅支持 plugin / history / all");
            }

            if (!await RebuildLock.WaitAsync(0))
            {
                return Results.Conflict("已有 Rebuild 任务正在执行，请稍后再试。");
            }

            try
            {
                List<RebuildResultDto> results = [];

                if (target is "plugin" or "all")
                {
                    long before = pluginDb.GetDatabaseFileSize();
                    await pluginDb.RebuildAsync();
                    long after = pluginDb.GetDatabaseFileSize();
                    long reduced = before - after;
                    results.Add(new RebuildResultDto(
                        Target: "plugin",
                        BeforeBytes: before,
                        BeforeSize: Format.FormatFileSize(before),
                        AfterBytes: after,
                        AfterSize: Format.FormatFileSize(after),
                        ReducedBytes: reduced,
                        ReducedSize: Format.FormatFileSize(Math.Abs(reduced)) + (reduced >= 0 ? " (已释放)" : " (增大)")));
                }

                if (target is "history" or "all")
                {
                    long before = historyRecorder.GetDatabaseFileSize();
                    await historyRecorder.RebuildAsync();
                    long after = historyRecorder.GetDatabaseFileSize();
                    long reduced = before - after;
                    results.Add(new RebuildResultDto(
                        Target: "history",
                        BeforeBytes: before,
                        BeforeSize: Format.FormatFileSize(before),
                        AfterBytes: after,
                        AfterSize: Format.FormatFileSize(after),
                        ReducedBytes: reduced,
                        ReducedSize: Format.FormatFileSize(Math.Abs(reduced)) + (reduced >= 0 ? " (已释放)" : " (增大)")));
                }

                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                var baseMsg = ex.GetBaseException().Message ?? ex.Message;
                bool isLoop = baseMsg.Contains("loop", StringComparison.OrdinalIgnoreCase)
                    || baseMsg.Contains("Detected loop", StringComparison.OrdinalIgnoreCase);
                app.Logger.LogError(ex, "数据库 Rebuild 失败 target={Target}", target);
                if (isLoop)
                {
                    return Results.Problem(
                        $"Rebuild 失败：检测到索引损坏（{baseMsg}）。已尝试容错重建仍失败。建议：1) 立即备份 {_dbPaths(historyRecorder, pluginDb)} 2) 停止 Bot 后用 LiteDB Studio 打开文件并尝试修复，或删除重建该库（历史库可删除 group_history.db 后重启自动重建，插件库删除会丢失配置/记忆）。详细日志见服务端。",
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }
                return Results.Problem($"Rebuild 失败: {baseMsg}", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // 轻量 Checkpoint：截断 WAL/journal，不重建索引，适合在 Rebuild 损坏时回收部分空间
        group.MapPost("/checkpoint", async (RebuildRequest? body) =>
        {
            string target = (body?.Target ?? "all").Trim().ToLowerInvariant();
            if (target is not ("plugin" or "history" or "all"))
                return Results.BadRequest("target 仅支持 plugin / history / all");
            if (!await RebuildLock.WaitAsync(0))
                return Results.Conflict("已有维护任务正在执行，请稍后再试。");
            try
            {
                List<RebuildResultDto> results = [];
                if (target is "plugin" or "all")
                {
                    long before = pluginDb.GetDatabaseFileSize();
                    await pluginDb.CheckpointAsync();
                    long after = pluginDb.GetDatabaseFileSize();
                    long reduced = before - after;
                    results.Add(new RebuildResultDto("plugin", before, Format.FormatFileSize(before), after, Format.FormatFileSize(after), reduced, Format.FormatFileSize(Math.Abs(reduced)) + (reduced >= 0 ? " (已释放)" : " (增大)")));
                }
                if (target is "history" or "all")
                {
                    long before = historyRecorder.GetDatabaseFileSize();
                    await historyRecorder.CheckpointAsync();
                    long after = historyRecorder.GetDatabaseFileSize();
                    long reduced = before - after;
                    results.Add(new RebuildResultDto("history", before, Format.FormatFileSize(before), after, Format.FormatFileSize(after), reduced, Format.FormatFileSize(Math.Abs(reduced)) + (reduced >= 0 ? " (已释放)" : " (增大)")));
                }
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Checkpoint 失败 target={Target}", target);
                return Results.Problem($"Checkpoint 失败: {ex.GetBaseException().Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
            finally { RebuildLock.Release(); }
        });
    }

    private static string _dbPaths(HistoryRecorder historyRecorder, PluginStorageDatabase pluginDb)
        => $"plugin_data.db({pluginDb.GetDatabaseSize()}), group_history.db({historyRecorder.GetDatabaseSize()})";
}
