using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace MerryBot.WebUI.Api;

public sealed record LogContentDto(string? File, IReadOnlyList<string> Lines);

/// <summary>日志管理 API：读取当前（最后写入的）NLog 日志文件末尾若干行。</summary>
public static class LogApiMapper
{
    private const int DefaultLines = 500;
    private const int MinLines = 100;
    private const int MaxLines = 2000;
    private const long MaxScanBytes = 64 * 1024 * 1024;
    private const int ChunkSize = 8192;

    public static void Map(WebApplication app, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logDirectory);

        app.MapGet("/api/logs/current", (int? lines) =>
        {
            var count = Math.Clamp(lines ?? DefaultLines, MinLines, MaxLines);
            var currentFile = FindCurrentLogFile(logDirectory);
            if (currentFile == null)
            {
                return Results.Ok(new LogContentDto(null, Array.Empty<string>()));
            }

            var logLines = ReadTail(currentFile, count);
            return Results.Ok(new LogContentDto(Path.GetFileName(currentFile), logLines));
        });
    }

    /// <summary>日志目录下最后写入的 *.log（即当前启动的日志）；目录不存在或为空返回 null。</summary>
    private static string? FindCurrentLogFile(string logDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(logDirectory);
            if (!directory.Exists)
            {
                return null;
            }
            return directory.EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从文件末尾向前扫描定位「倒数第 count 行」的起始偏移，再顺序读取到文件尾。
    /// 返回顺序为最新在前；文件读取中追加/删除均降级为已读内容。
    /// </summary>
    private static IReadOnlyList<string> ReadTail(string filePath, int count)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return lines;
            }

            // 多找两行作为缓冲，吸收文件末尾的空白行，最后再取 count 条非空行
            var target = count + 2;
            var buffer = new byte[ChunkSize];
            long scanPos = stream.Length;
            long startOffset = 0;
            var newlinesSeen = 0;
            var scannedBytes = 0L;

            while (scanPos > 0 && newlinesSeen < target && scannedBytes < MaxScanBytes)
            {
                var readSize = (int)Math.Min(scanPos, ChunkSize);
                scanPos -= readSize;
                stream.Seek(scanPos, SeekOrigin.Begin);
                var read = stream.Read(buffer, 0, readSize);
                if (read <= 0)
                {
                    break;
                }
                scannedBytes += read;

                for (var i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }
                    newlinesSeen++;
                    if (newlinesSeen >= target)
                    {
                        startOffset = scanPos + i + 1;
                        break;
                    }
                }
                if (newlinesSeen < target)
                {
                    startOffset = scanPos;
                }
            }

            stream.Seek(startOffset, SeekOrigin.Begin);
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: ChunkSize, leaveOpen: true))
            {
                while (lines.Count < target)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    lines.Add(line);
                }
            }

            // 去掉空白行，取最后 count 条，最新在前
            var nonEmpty = lines.Where(static line => line.Length > 0).ToList();
            if (nonEmpty.Count > count)
            {
                nonEmpty = nonEmpty.GetRange(nonEmpty.Count - count, count);
            }
            nonEmpty.Reverse();
            return nonEmpty;
        }
        catch (Exception)
        {
            return lines;
        }
    }
}
