using DataService;
using HistoryWebFrontend.Components;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Mvc;

namespace HistoryWebFrontend;

public class Program
{
    public static void Main()
    {
        string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
        var historyRecorder = new HistoryRecorder($"{dataPath}/group_history.db", $"{dataPath}/storage");
        var app = CreateApp(historyRecorder);
        app.Run();
    }
    public static WebApplication CreateApp(HistoryRecorder historyRecorder, string webAddress="http://0.0.0.0:5000")
    {
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

        return app;
    }

    private static string GetImageContentType(string url)
    {
        var extension = Path.GetExtension(url).ToLower();
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

    private static string GetFileContentType(string url)
    {
        var extension = Path.GetExtension(url).ToLower();
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

    private static string GetFileName(string url)
    {
        var uri = new Uri(url);
        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrEmpty(fileName) ? "download" : fileName;
    }
}
