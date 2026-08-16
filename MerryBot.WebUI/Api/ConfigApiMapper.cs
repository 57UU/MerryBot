using CommonLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>暴露已注册配置的读取、保存、重启与重载 API。</summary>
public static class ConfigApiMapper
{
    public static void Map(WebApplication app, ConfigRegistry registry, Action<int> shutdown)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(shutdown);

        var group = app.MapGroup("/api/config");
        group.MapGet("/", () => Results.Ok(new ConfigPanelDto(registry.GetSnapshot())));
        group.MapPost("/{id}", async (string id, ConfigUpdateRequest body) =>
        {
            try
            {
                await registry.SaveAsync(id, body.Fields);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Text(ex.Message);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "保存配置 {ConfigId} 失败。", id);
                return Results.Problem("配置保存失败。", statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        group.MapPost("/restart", () => RestartAfterResponse(shutdown, ExitCode.RESTART));
        group.MapPost("/reload", () => RestartAfterResponse(shutdown, ExitCode.RELOAD));
    }

    private static IResult RestartAfterResponse(Action<int> shutdown, int exitCode)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            shutdown(exitCode);
        });
        return Results.Ok(new { restarting = true });
    }

    private static IResult Text(string message)
        => Results.Text(message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);

    private sealed record ConfigUpdateRequest(Dictionary<string, System.Text.Json.JsonElement> Fields);
}
