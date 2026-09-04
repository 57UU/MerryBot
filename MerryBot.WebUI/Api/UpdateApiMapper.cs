using MerryBot.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>检测更新与一键更新 API；执行逻辑位于宿主 core（<see cref="IHostLifecycle"/>）。</summary>
public static class UpdateApiMapper
{
    public static void Map(WebApplication app, IHostLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(lifecycle);

        var group = app.MapGroup("/api/update");
        group.MapPost("/check", (CancellationToken cancellationToken) => lifecycle.CheckUpdateAsync(cancellationToken));
        group.MapPost("", () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500);
                    await lifecycle.RequestUpdateAsync(force: false);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "请求更新失败");
                }
            });
            return Results.Ok(new { updating = true });
        });
    }
}
