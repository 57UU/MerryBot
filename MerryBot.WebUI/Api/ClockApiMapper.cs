using Agent.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>
/// 定时任务管理 HTTP API（core 拥有调度器，直接映射；不依赖任何插件接口）。
/// 编辑/删除按 (pluginId, sessionId, taskId) 所有权校验——前端从任务列表取回归属后随请求回传。
/// Content 更新仅接受文本（string）：避免管理端把插件自定义模型覆盖成错误类型；
/// 对象型内容请回到插件侧修改。
/// </summary>
public static class ClockApiMapper
{
    public static void Map(WebApplication app, ClockService clockService)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(clockService);

        var routes = app.MapGroup("/api/clock");
        routes.MapGet("/tasks", async (CancellationToken cancellationToken) =>
            Results.Ok((await clockService.ListAllAsync(cancellationToken))
                .Select(ClockTaskDto.From)
                .ToList()));

        routes.MapPost("/tasks/update", async (ClockTaskUpdateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var task = await clockService.UpdateAsync(
                    request.PluginId,
                    request.SessionId,
                    request.TaskId,
                    new ClockUpdateRequest
                    {
                        CronExpression = request.CronExpression,
                        TimeZoneId = request.TimeZoneId,
                        // ContentProvided=false 时不修改内容；true 且文本非空白时以文本替换。
                        // 注意：空文本无法表达"清空为 null"——ClockUpdateRequest 语义约定 null = 不修改
                        Content = request.ContentProvided && !string.IsNullOrWhiteSpace(request.Content)
                            ? request.Content
                            : null,
                        RunOnce = request.RunOnce,
                        TimeoutSeconds = request.TimeoutSeconds,
                        Enabled = request.Enabled,
                    },
                    cancellationToken);
                return Results.Ok(ClockTaskDto.From(task));
            }
            catch (Exception exception) { return ToError(exception); }
        });

        routes.MapPost("/tasks/delete", async (ClockTaskDeleteRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await clockService.DeleteAsync(request.PluginId, request.SessionId, request.TaskId, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });

        routes.MapGet("/logs", async (
            string pluginId,
            string sessionId,
            Guid? taskId,
            string? status,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var logs = await clockService.QueryLogsAsync(
                    pluginId,
                    sessionId,
                    new ClockLogQuery
                    {
                        TaskId = taskId,
                        Status = ParseStatus(status),
                        FromUtc = from,
                        ToUtc = to,
                        Limit = limit ?? 20,
                    },
                    cancellationToken);
                return Results.Ok(logs.Select(ClockLogDto.From).ToList());
            }
            catch (Exception exception) { return ToError(exception); }
        });
    }

    private static ClockRunStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        // 拒绝纯数字字符串：Enum.TryParse 会把数字解析成枚举值，掩盖非法状态输入
        if (long.TryParse(value, out _)
            || !Enum.TryParse<ClockRunStatus>(value, ignoreCase: true, out var status)
            || !Enum.IsDefined(status))
        {
            throw new ArgumentException($"未知执行状态: {value}");
        }
        return status;
    }

    private static IResult ToError(Exception exception)
    {
        // 统一日志出口（NLog）：WebUI API 错误可在 /logs 页查看，避免静默失败
        CommonLib.SimpleLog.Default.Warn(exception, $"WebUI Clock API 请求失败: {exception.Message}");
        return exception switch
        {
            ArgumentException or ArgumentOutOfRangeException => Results.BadRequest(exception.Message),
            KeyNotFoundException => Results.NotFound(exception.Message),
            InvalidOperationException => Results.Conflict(exception.Message),
            _ => Results.Problem(exception.Message),
        };
    }
}
