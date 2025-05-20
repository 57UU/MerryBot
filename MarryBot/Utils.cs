using System.Text;

namespace MarryBot;

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
        catch (Exception e)
        {
            return null;
        }
    }
    public static IEnumerable<string> readDir(string path)
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
    public async static Task WaitForever()
    {
        while (true)
        {
            await Task.Delay(int.MaxValue);
        }
    }
}
