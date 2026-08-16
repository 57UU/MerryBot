using CommonLib;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>上下文快照 HTTP API；仅依赖管理接口，群名只由 Core 历史库提供显示映射。</summary>
public static class ContextSnapshotApiMapper
{
    public static void Map(WebApplication app, IContextSnapshotService manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var routes = app.MapGroup("/api/context");
        routes.MapGet("/sessions", async (CancellationToken cancellationToken) =>
        {
            var sessionsTask = manager.ListSessionsAsync(cancellationToken);
            var namesTask = historyRecorder.GetAllGroupNamesAsync();
            await Task.WhenAll(sessionsTask, namesTask);
            var names = namesTask.Result.ToDictionary(item => item.GroupId, item => item.Name);
            return Results.Ok(sessionsTask.Result.Select(session => new ContextSessionDto(
                session.SessionKey,
                GetDisplayName(session.SessionKey, names),
                session.MessageCount,
                session.UpdatedAtUtc)));
        });
        routes.MapGet("/snapshot", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await manager.GetSnapshotAsync(sessionKey, cancellationToken);
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
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

    private static IResult ToError(Exception exception) => exception switch
    {
        ArgumentException or ArgumentOutOfRangeException => Results.BadRequest(exception.Message),
        _ => Results.Problem(exception.Message),
    };
}
