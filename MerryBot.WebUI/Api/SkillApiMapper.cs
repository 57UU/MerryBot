using CommonLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>Skill 管理 HTTP API；仅依赖管理接口，不依赖 AgentPlugin 的具体实现。</summary>
public static class SkillApiMapper
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    public static void Map(WebApplication app, ISkillManagementService manager)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);

        var routes = app.MapGroup("/api/skills");
        routes.MapGet("/", async (CancellationToken cancellationToken) =>
            Results.Ok(await manager.ListSkillsAsync(cancellationToken)));
        routes.MapGet("/content", async (string name, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await manager.ReadSkillAsync(name, includeDisabled: true, cancellationToken)); }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/upload", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var file = form.Files.GetFile("file");
                if (file == null || file.Length == 0) return Results.BadRequest("请选择要上传的 Skill 文件。");
                if (file.Length > MaxUploadBytes) return Results.BadRequest("Skill 上传文件不能超过 20 MB。");
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                await manager.UploadSkillAsync(new SkillUpload(file.FileName, buffer.ToArray()), cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/enabled", async (SkillEnabledRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.SetSkillEnabledAsync(request.Name, request.Enabled, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
        routes.MapPost("/delete", async (string name, CancellationToken cancellationToken) =>
        {
            try
            {
                await manager.DeleteSkillAsync(name, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) { return ToError(exception); }
        });
    }

    private static IResult ToError(Exception exception)
    {
        // 统一日志出口（NLog）：WebUI API 错误可在 /logs 页查看，避免静默失败
        CommonLib.SimpleLog.Default.Warn(exception, $"WebUI Skill API 请求失败: {exception.Message}");
        return exception switch
        {
            ArgumentException or ArgumentOutOfRangeException => Results.BadRequest(exception.Message),
            KeyNotFoundException => Results.NotFound(exception.Message),
            InvalidOperationException => Results.Conflict(exception.Message),
            _ => Results.Problem(exception.Message),
        };
    }
}
