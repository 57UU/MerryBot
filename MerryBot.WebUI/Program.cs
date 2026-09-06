using System.Net;
using DataService;
using MerryBot.WebUI.Components;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MerryBot.WebUI;

public class Program
{
    public static async Task Main()
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        var historyRecorder = new HistoryRecorder(Path.Combine(dataPath, "group_history.db"), Path.Combine(dataPath, "storage"));
        var app = CreateApp(historyRecorder);
        await app.RunAsync();
    }
    public static WebApplication CreateApp(HistoryRecorder historyRecorder, string webAddress="http://localhost:5000",
        Action<IServiceCollection>? configureServices = null,
        bool disableProcessSignalHandling = false)
    {
        // 执行数据库 schema 迁移（幂等，已是最新版本时直接返回）
        historyRecorder.MigrateAsync().GetAwaiter().GetResult();

        var webAssembly = typeof(Program).Assembly;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions()
        {
            ApplicationName = webAssembly.GetName().Name,
        });

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        // Blazor Server 场景下先注册默认 HttpClient，再注册 Fluent UI 组件服务
        builder.Services.AddHttpClient();
        builder.Services.AddFluentUIComponents();
        // data
        builder.Services.AddSingleton(historyRecorder);

        // 调大 JS 互操作的消息上限：上下文快照/日志等大 JSON 经 SignalR 回传时，
        // 默认 32KB 上限会导致连接被断开（表现为 TaskCanceledException + 断线重连）。
        // 2MB 足够容纳较大的快照/日志，同时避免无限放宽。
        builder.Services.Configure<HubOptions>(options =>
            options.MaximumReceiveMessageSize = 2 * 1024 * 1024);

        // 宿主（Logic）注入的进程内服务（如 IContextSnapshotService）
        configureServices?.Invoke(builder.Services);

        if (disableProcessSignalHandling)
        {
            // 内嵌模式由 MerryBot 外层统一接收 Ctrl+C/SIGTERM；否则默认的
            // ConsoleLifetime 会与外层的 Logic.Shutdown 同时执行关闭流程。
            builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();
        }

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();


#if DEBUG
        app.MapStaticAssets();
#else
        app.UseStaticFiles();
#endif
        StaticWebAssetsLoader.UseStaticWebAssets(app.Environment, app.Configuration);

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Urls.Add(webAddress);

        // WebUI 无内置鉴权（by design）：非本地回环监听意味着局域网/公网可直达全部管理 API，启动时明确告警
        if (!IsLoopbackAddress(webAddress))
        {
            app.Logger.LogWarning(
                "WebUI 正在非本地地址上监听 {WebAddress}：WebUI 无内置鉴权，所有管理 API（含配置/Key/重启/更新）将对该网络可见。"
                + "建议仅绑定 localhost 并经 SSH 端口转发远程管理；如确需远程访问，请经受控内网或 HTTPS 反向代理保护，风险自担。",
                webAddress);
        }

        // 图片API
        app.MapGet("/api/image/{id}", async (long id, HistoryRecorder historyRecorder) =>
        {
            var image = await historyRecorder.GetImageByIdAsync(id);
            if (image == null)
            {
                return Results.NotFound();
            }

            var data = await historyRecorder.GetImageDataAsync(image.Hash);
            if (data == null)
            {
                return Results.NotFound();
            }

            var contentType = GetImageContentType(image.FileType);
            return Results.File(data, contentType);
        });

        app.MapGet("/api/file/{id}", async (long id, [FromQuery] string? name, HistoryRecorder historyRecorder) =>
        {
            var file = await historyRecorder.GetFileByIdAsync(id);
            if (file == null)
            {
                return Results.NotFound();
            }

            var data = await historyRecorder.GetFileDataAsync(file.Hash);
            if (data == null)
            {
                return Results.NotFound();
            }

            var contentType = GetFileContentType(file.FileType);
            var fileName = name ?? id.ToString();
            return Results.File(data, contentType, fileName);
        });

        // 新版处理链只保存 merrybot://resource/... 本地 URI；前端绝不直连远端 URL。
        app.MapGet("/api/resource", async ([FromQuery] string reference, HistoryRecorder historyRecorder) =>
        {
            if (string.IsNullOrWhiteSpace(reference)) return Results.BadRequest();
            var resource = await historyRecorder.GetResourceReferenceAsync(reference);
            if (resource?.StoredObjectId is not long objectId)
            {
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            if (resource.IsImage)
            {
                var image = await historyRecorder.GetImageByIdAsync(objectId);
                if (image == null) return Results.NotFound();
                var data = await historyRecorder.GetImageDataAsync(image.Hash);
                var ext = string.IsNullOrEmpty(image.FileType) ? (resource.OriginalName ?? resource.Source) : image.FileType;
                return data == null ? Results.NotFound() : Results.File(data, GetImageContentType(ext));
            }

            var file = await historyRecorder.GetFileByIdAsync(objectId);
            if (file == null) return Results.NotFound();
            var fileData = await historyRecorder.GetFileDataAsync(file.Hash);
            var fileExt = string.IsNullOrEmpty(file.FileType) ? (resource.OriginalName ?? resource.Source) : file.FileType;
            return fileData == null
                ? Results.NotFound()
                : Results.File(fileData, GetFileContentType(fileExt), resource.OriginalName ?? objectId.ToString());
        });

        return app;
    }

    /// <summary>
    /// 判断监听地址是否为本地回环（localhost / 127.x / ::1）。
    /// 解析失败时返回 true（交由后续启动流程报错，避免误报）。
    /// </summary>
    private static bool IsLoopbackAddress(string webAddress)
    {
        if (!Uri.TryCreate(webAddress, UriKind.Absolute, out Uri? uri))
        {
            return true;
        }
        string host = uri.Host.Trim().Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return IPAddress.IsLoopback(address);
        }
        return false;
    }

    private static string GetImageContentType(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "image/jpeg";
        }
        var extension = Path.GetExtension(url).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    private static string GetFileContentType(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "application/octet-stream";
        }
        var extension = Path.GetExtension(url).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".doc" or ".docx" => "application/msword",
            ".xls" or ".xlsx" => "application/vnd.ms-excel",
            ".ppt" or ".pptx" => "application/vnd.ms-powerpoint",
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".txt" => "text/plain",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }
}
