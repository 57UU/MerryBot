using CommonLib;
using MerryBot.Contracts;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>记忆管理 HTTP API；存取经由接口完成，群名只由 Core 历史库提供显示映射。</summary>
public static class MemoryApiMapper
{
    public static void Map(WebApplication app, IMemoryManagementService manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var routes = app.MapGroup("/api/memories");
        routes.MapGet("/sessions", async (CancellationToken cancellationToken) =>
        {
            var sessionsTask = manager.ListMemorySessionsAsync(cancellationToken);
            var namesTask = historyRecorder.GetAllGroupNamesAsync();
            await Task.WhenAll(sessionsTask, namesTask);
            var names = namesTask.Result.ToDictionary(item => item.GroupId, item => item.Name);
            return Results.Ok(sessionsTask.Result.Select(session => new MemorySessionDto(
                session.SessionKey,
                GetDisplayName(session.SessionKey, names),
                session.UpdatedAtUtc)));
        });
        routes.MapGet("/index", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await manager.GetMemoryIndexAsync(sessionKey, cancellationToken)); }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/index", async (MemoryIndexUpdateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.SaveMemoryIndexAsync(request.SessionKey, request.Content, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapGet("/entries", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await manager.ListMemoriesAsync(sessionKey, cancellationToken)); }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/entries", async (MemoryEntryUpdateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.SaveMemoryAsync(request.SessionKey, request.Key, request.Content, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        // 删除统一幂等 204：404 会被 UseStatusCodePagesWithReExecute 以原方法重执行到 /not-found 产生 405
        routes.MapPost("/entries/delete", async (string sessionKey, string key, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.DeleteMemoryAsync(sessionKey, key, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
    }

    private static string GetDisplayName(string sessionKey, IReadOnlyDictionary<long, string> names)
    {
        var parts = sessionKey.Split('/', StringSplitOptions.None);
        if (parts is ["qq", "group", var id] && long.TryParse(id, out var groupId))
        {
            return names.TryGetValue(groupId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? $"{name} ({groupId})"
                : $"群 {groupId}";
        }
        return sessionKey;
    }

    private static IResult ToError(Exception exception)
    {
        // 统一日志出口（NLog）：WebUI API 错误可在 /logs 页查看，避免静默失败
        CommonLib.SimpleLog.Default.Warn(exception, $"WebUI Memory API 请求失败: {exception.Message}");
        return exception switch
        {
            ArgumentException or ArgumentOutOfRangeException => Results.BadRequest(exception.Message),
            _ => Results.Problem(exception.Message),
        };
    }
}
