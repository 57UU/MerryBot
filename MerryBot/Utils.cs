namespace MerryBot;

public static class Utils
{
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
    public static bool CreateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空或空白", nameof(path));

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return true;
        }
        return false;
    }
}
