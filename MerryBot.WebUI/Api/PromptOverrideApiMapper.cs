using CommonLib;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>按群系统提示词复写 HTTP API；存取经由接口完成，群名只由 Core 历史库提供显示映射。
/// 未复写的会话回退全局 AgentConfig.AiPrompt（仍在配置中心编辑）。</summary>
public static class PromptOverrideApiMapper
{
    public static void Map(WebApplication app, IPromptOverrideService manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var routes = app.MapGroup("/api/prompts");
        routes.MapGet("/overrides", async (CancellationToken cancellationToken) =>
        {
            var overridesTask = manager.ListOverridesAsync(cancellationToken);
            var namesTask = historyRecorder.GetAllGroupNamesAsync();
            await Task.WhenAll(overridesTask, namesTask);
            var names = namesTask.Result.ToDictionary(item => item.GroupId, item => item.Name);
            return Results.Ok(overridesTask.Result.Select(entry => new PromptOverrideSessionDto(
                entry.SessionKey,
                GetDisplayName(entry.SessionKey, names),
                entry.UpdatedAtUtc)));
        });
        routes.MapGet("/override", async (string sessionKey, CancellationToken cancellationToken) =>
        {
            try
            {
                var entry = await manager.GetOverrideAsync(sessionKey, cancellationToken);
                return Results.Ok(entry is null
                    ? new PromptOverrideDetailDto(sessionKey, false, string.Empty, null)
                    : new PromptOverrideDetailDto(entry.SessionKey, true, entry.Prompt, entry.UpdatedAtUtc));
            }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/override", async (PromptOverrideSaveRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                // 空白视为回退全局：与删除语义一致，避免存入空提示词导致空 system prompt
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    await manager.DeleteOverrideAsync(request.SessionKey, cancellationToken);
                }
                else
                {
                    await manager.SaveOverrideAsync(request.SessionKey, request.Prompt, cancellationToken);
                }
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        // 删除统一幂等 204：404 会被 UseStatusCodePagesWithReExecute 以原方法重执行到 /not-found 产生 405
        routes.MapPost("/override/delete", async (string sessionKey, CancellationToken cancellationToken) =>
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
