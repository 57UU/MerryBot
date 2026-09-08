using MerryBot.WebUI.Api;

namespace MerryBot.Test;

/// <summary>
/// LogFileService：日志文件末尾扫描与级别/关键词过滤。
/// 约定：返回行为最新在前；TRACE 并入 DEBUG；无级别行（堆栈续行）归为 DEBUG。
/// </summary>
public sealed class LogFileServiceTests
{
    private static string WriteLog(string directory, string fileName, params string[] lines)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }

    private static string CreateDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "merrybot-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string LogLine(string level, string message)
        => $"2026-09-09 10:00:00.000|{level}|test|{message}";

    [Fact]
    public void Empty_Directory_Returns_Null_File_And_No_Lines()
    {
        string directory = CreateDir();
        try
        {
            LogFileService service = new(directory);

            LogContentDto content = service.ReadCurrent(null, null, null, null);

            Assert.Null(content.File);
            Assert.Empty(content.Lines);
            Assert.Empty(service.ListFiles());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ReadCurrent_Returns_Latest_First()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot-2026-09-09.log",
                LogLine("INFO", "first"),
                LogLine("INFO", "second"),
                LogLine("INFO", "third"));
            LogFileService service = new(directory);

            LogContentDto content = service.ReadCurrent(null, null, null, null);

            Assert.Equal("bot-2026-09-09.log", content.File);
            Assert.Equal(3, content.Lines.Count);
            Assert.Contains("third", content.Lines[0]);
            Assert.Contains("first", content.Lines[2]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Level_Filter_Keeps_Matching_Levels_Only()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log",
                LogLine("INFO", "info"),
                LogLine("ERROR", "error"),
                LogLine("FATAL", "fatal"),
                LogLine("WARN", "warn"));
            LogFileService service = new(directory);

            LogContentDto content = service.ReadCurrent(null, "ERROR", null, null);

            Assert.Single(content.Lines);
            Assert.Contains("error", content.Lines[0]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Trace_Is_Merged_Into_Debug()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log",
                LogLine("TRACE", "trace"),
                LogLine("DEBUG", "debug"),
                LogLine("INFO", "info"));
            LogFileService service = new(directory);

            // TRACE 在 DEBUG 过滤下命中（与页面着色一致）
            LogContentDto debug = service.ReadCurrent(null, "DEBUG", null, null);
            Assert.Equal(2, debug.Lines.Count);

            // TRACE 作为过滤条件本身不命中任何行（DetectLevel 从不返回 TRACE）
            LogContentDto trace = service.ReadCurrent(null, "TRACE", null, null);
            Assert.Empty(trace.Lines);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Lines_Without_Level_Are_Treated_As_Debug()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log",
                LogLine("ERROR", "boom"),
                "   at Foo.Bar() 堆栈续行无级别");
            LogFileService service = new(directory);

            LogContentDto debug = service.ReadCurrent(null, "DEBUG", null, null);
            Assert.Single(debug.Lines);
            Assert.Contains("Foo.Bar", debug.Lines[0]);

            LogContentDto error = service.ReadCurrent(null, "ERROR", null, null);
            Assert.Single(error.Lines);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Keyword_Filter_Is_Case_Insensitive()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log",
                LogLine("INFO", "HelloWorld 启动"),
                LogLine("INFO", "无关行"));
            LogFileService service = new(directory);

            LogContentDto content = service.ReadCurrent(null, null, "helloworld", null);

            Assert.Single(content.Lines);
            Assert.Contains("HelloWorld", content.Lines[0]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Level_And_Keyword_Combine()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log",
                LogLine("INFO", "target"),
                LogLine("ERROR", "target"),
                LogLine("ERROR", "other"));
            LogFileService service = new(directory);

            LogContentDto content = service.ReadCurrent(null, "ERROR", "target", null);

            Assert.Single(content.Lines);
            Assert.Contains("ERROR", content.Lines[0]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Requested_Lines_Are_Clamped_And_Take_Tail()
    {
        string directory = CreateDir();
        try
        {
            string[] lines = Enumerable.Range(0, 150).Select(i => LogLine("INFO", $"line-{i:D3}")).ToArray();
            WriteLog(directory, "bot.log", lines);
            LogFileService service = new(directory);

            // 请求 120 行：返回末尾 120 行，最新在前
            LogContentDto content = service.ReadCurrent(120, null, null, null);

            Assert.Equal(120, content.Lines.Count);
            Assert.Contains("line-149", content.Lines[0]);
            Assert.Contains("line-030", content.Lines[119]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Explicit_File_Reads_History_File()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot-2026-09-07.log", LogLine("INFO", "old"));
            string newPath = WriteLog(directory, "bot-2026-09-09.log", LogLine("INFO", "new"));
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);
            LogFileService service = new(directory);

            // 缺省读当前文件
            Assert.Contains("new", service.ReadCurrent(null, null, null, null).Lines[0]);

            // 指定文件名读历史文件
            LogContentDto history = service.ReadCurrent(null, null, null, "bot-2026-09-07.log");
            Assert.Equal("bot-2026-09-07.log", history.File);
            Assert.Contains("old", Assert.Single(history.Lines));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Unknown_Or_Traversal_File_Returns_Null()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log", LogLine("INFO", "x"));
            LogFileService service = new(directory);

            // 按文件名精确匹配 *.log：穿越路径与不存在的文件都不命中
            Assert.Null(service.ReadCurrent(null, null, null, "../bot.log").File);
            Assert.Null(service.ReadCurrent(null, null, null, "missing.log").File);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ListFiles_Marks_Current_And_Sorts_By_Time_Desc()
    {
        string directory = CreateDir();
        try
        {
            string oldPath = WriteLog(directory, "bot-2026-09-07.log", LogLine("INFO", "old"));
            string newPath = WriteLog(directory, "bot-2026-09-09.log", LogLine("INFO", "new"));
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);
            LogFileService service = new(directory);

            List<LogFileInfoDto> files = service.ListFiles().ToList();

            Assert.Equal(2, files.Count);
            Assert.Equal("bot-2026-09-09.log", files[0].Name);
            Assert.True(files[0].IsCurrent);
            Assert.False(files[1].IsCurrent);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Multibyte_Lines_Are_Not_Corrupted()
    {
        string directory = CreateDir();
        try
        {
            WriteLog(directory, "bot.log", LogLine("INFO", "中文日志行：群 12345 收到消息"));
            LogFileService service = new(directory);

            string line = Assert.Single(service.ReadCurrent(null, null, null, null).Lines);

            Assert.Contains("中文日志行：群 12345 收到消息", line);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
