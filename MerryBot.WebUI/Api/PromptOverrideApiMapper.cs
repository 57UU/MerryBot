using CommonLib;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>按群提示词 override 管理 HTTP API；存取经由接口完成，群名只由 Core 历史库提供显示映射。</summary>
public static class PromptOverrideApiMapper
{
    public static void Map(WebApplication app, IPromptOverrideService manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var routes = app.MapGroup("/api/prompt-overrides");
        routes.MapGet("/", async (CancellationToken cancellationToken) =>
        {
            var overridesTask = manager.ListOverridesAsync(cancellationToken);
            var namesTask = historyRecorder.GetAllGroupNamesAsync();
            await Task.WhenAll(overridesTask, namesTask);
            var names = namesTask.Result.ToDictionary(item => item.GroupId, item => item.Name);
            return Results.Ok(overridesTask.Result.Select(item => new PromptOverrideSessionDto(
                item.SessionKey,
                GetDisplayName(item.SessionKey, names),
                item.UpdatedAtUtc)));
        });
        routes.MapGet("/content", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await manager.GetOverrideAsync(sessionKey, cancellationToken)); }
            catch (Exception exception) { return ToError(exception); }
        });
        // 保存统一 204：空内容视为删除（回退全局提示词）
        routes.MapPost("/save", async (PromptOverrideSaveRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.SaveOverrideAsync(request.SessionKey, request.Content, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        // 删除统一幂等 204：404 会被 UseStatusCodePagesWithReExecute 以原方法重执行到 /not-found 产生 405
        routes.MapPost("/delete", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.DeleteOverrideAsync(sessionKey, cancellationToken);
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
        CommonLib.SimpleLog.Default.Warn(exception, $"WebUI PromptOverride API 请求失败: {exception.Message}");
        return exception switch
        {
            ArgumentException or ArgumentOutOfRangeException => Results.BadRequest(exception.Message),
            _ => Results.Problem(exception.Message),
        };
    }
}
