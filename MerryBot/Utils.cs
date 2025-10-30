using System.Text;

namespace MerryBot;

public static class Utils
{
    public async static Task<Exception?> write(string path, string data)
    {
        try
        {
            StreamWriter writer = new StreamWriter(path, false, encoding: Encoding.UTF8);
            await writer.WriteAsync(data);
            writer.Close();

            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
    public async static Task<string?> read(string path)
    {
        try
        {
            StreamReader reader = new StreamReader(path, encoding: Encoding.UTF8);
            var result = await reader.ReadToEndAsync();
            reader.Close();
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }
    public static IEnumerable<string> ReadDir(string path)
    {
        var realPath = path;
        DirectoryInfo dirInfo = new DirectoryInfo(realPath);
        if (!dirInfo.Exists)
        {
            dirInfo.Create();
        }
        var result = from i in dirInfo.EnumerateFiles() select i.Name;
        return result;
    }
    public static string GenerateFileNameByCurrentTime()
    {
        var t = DateTime.Now;
        return $"{t.Year}-{t.Month}-{t.Day}_{t.Hour}-{t.Minute}-{t.Second}";
    }
    
    // 旧方法，保留以保持向后兼容
    [Obsolete("Use WaitForShutdownAsync instead for more elegant async shutdown handling")]
    public async static Task WaitForever()
    {
        while (true)
        {
            await Task.Delay(int.MaxValue);
        }
    }
    
    public static async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常的取消，不需要处理
        }
    }
}
