using DataService;
using MerryBot.WebUI.Components;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Mvc;

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
    public static WebApplication CreateApp(HistoryRecorder historyRecorder, string webAddress="http://localhost:5000")
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
        // data
        builder.Services.AddSingleton(historyRecorder);

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

            var contentType = GetImageContentType(image.OriginalUrl);
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

            var contentType = GetFileContentType(file.OriginalUrl);
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
                return data == null ? Results.NotFound() : Results.File(data, GetImageContentType(image.OriginalUrl));
            }

            var file = await historyRecorder.GetFileByIdAsync(objectId);
            if (file == null) return Results.NotFound();
            var fileData = await historyRecorder.GetFileDataAsync(file.Hash);
            return fileData == null
                ? Results.NotFound()
                : Results.File(fileData, GetFileContentType(file.OriginalUrl), resource.OriginalName ?? objectId.ToString());
        });

        return app;
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
