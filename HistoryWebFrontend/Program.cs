using DataService;
using HistoryWebFrontend.Components;

namespace HistoryWebFrontend
{
    public class Program
    {
        public static void Main()
        {
            string dataPath = Environment.GetEnvironmentVariable("MERRY_BOT") ?? "data";
            var app=CreateApp(
                new AiMessageRecorder($"{dataPath}/ai_message.db"), 
                new HistoryRecorder($"{dataPath}/group_history.db")
                );
            app.Run();
        }
        public static WebApplication CreateApp(AiMessageRecorder aiMessageRecorder, HistoryRecorder historyRecorder)
        {
            var builder = WebApplication.CreateBuilder();

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            // data
            builder.Services.AddSingleton(aiMessageRecorder);
            builder.Services.AddSingleton(historyRecorder);

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // 图片API
            app.MapGet("/api/image/{id}", (long id, HistoryRecorder historyRecorder) =>
            {
                var image = historyRecorder.GetImageById(id);
                if (image == null)
                {
                    return Results.NotFound();
                }

                var contentType = GetImageContentType(image.OriginalUrl);
                return Results.File(image.Data, contentType);
            });

            // 文件API
            app.MapGet("/api/file/{id}", (long id, HistoryRecorder historyRecorder) =>
            {
                var file = historyRecorder.GetFileById(id);
                if (file == null)
                {
                    return Results.NotFound();
                }

                var contentType = GetFileContentType(file.OriginalUrl);
                var fileName = GetFileName(file.OriginalUrl);
                return Results.File(file.Data, contentType, fileName);
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
}
