using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace MerryBot.WebUI.Api;

public sealed record LogContentDto(string? File, IReadOnlyList<string> Lines);
public sealed record LogFileInfoDto(string Name, DateTimeOffset LastWriteTimeUtc, long SizeBytes, bool IsCurrent);

/// <summary>
/// 日志管理 API：读取 NLog 日志文件末尾若干行，支持按级别/关键词后端过滤
/// （向后多扫，避免前端过滤漏掉更早的匹配行）与历史日志文件浏览切换。
/// </summary>
public static class LogApiMapper
{
    private const int DefaultLines = 500;
    private const int MinLines = 100;
    private const int MaxLines = 2000;
    private const long MaxScanBytes = 64 * 1024 * 1024;
    private const int ChunkSize = 8192;
    private static readonly Regex LevelRegex = new(@"\b(TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\b", RegexOptions.Compiled);

    public static void Map(WebApplication app, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logDirectory);

        // level: 全部|TRACE|DEBUG|INFO|WARN|ERROR|FATAL（缺省/全部 = 不过滤；TRACE 并入 DEBUG 与页面一致）
        // keyword: 大小写不敏感子串（缺省空 = 不过滤）
        // file: 历史日志文件名（缺省 = 当前文件）
        app.MapGet("/api/logs/current", (int? lines, string? level, string? keyword, string? file) =>
        {
            var count = Math.Clamp(lines ?? DefaultLines, MinLines, MaxLines);
            var targetFile = ResolveLogFile(logDirectory, file);
            if (targetFile == null)
            {
                return Results.Ok(new LogContentDto(null, Array.Empty<string>()));
            }

            var logLines = ReadTail(targetFile, count, NormalizeLevel(level), NormalizeKeyword(keyword));
            return Results.Ok(new LogContentDto(Path.GetFileName(targetFile), logLines));
        });

        // 历史日志文件列表（按最后写入时间倒序；IsCurrent 为当前正在写入的文件）
        app.MapGet("/api/logs/files", () =>
        {
            var currentName = FindCurrentLogFile(logDirectory) is { } current ? Path.GetFileName(current) : null;
            var files = ListLogFiles(logDirectory)
                .Select(f => new LogFileInfoDto(
                    f.Name,
                    new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                    f.Length,
                    string.Equals(f.Name, currentName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return Results.Ok(files);
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

    /// <summary>按文件名精确匹配 *.log（杜绝路径穿越）；未指定时返回当前文件。</summary>
    private static string? ResolveLogFile(string logDirectory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FindCurrentLogFile(logDirectory);
        }
        try
        {
            var directory = new DirectoryInfo(logDirectory);
            if (!directory.Exists)
            {
                return null;
            }
            return directory.EnumerateFiles("*.log")
                .FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
                ?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<FileInfo> ListLogFiles(string logDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(logDirectory);
            if (!directory.Exists)
            {
                return Enumerable.Empty<FileInfo>();
            }
            return directory.EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return Enumerable.Empty<FileInfo>();
        }
    }

    private static string? NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level) || level == "全部")
        {
            return null;
        }
        return level.Trim().ToUpperInvariant();
    }

    private static string? NormalizeKeyword(string? keyword) =>
        string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

    private static bool Matches(string line, string? levelFilter, string? keywordFilter)
    {
        if (levelFilter != null && DetectLevel(line) != levelFilter)
        {
            return false;
        }
        if (keywordFilter != null && line.IndexOf(keywordFilter, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>宽松识别日志行级别；无级别行（异常堆栈续行等）归为 DEBUG（与页面着色一致）。</summary>
    private static string DetectLevel(string line)
    {
        var match = LevelRegex.Match(line);
        if (!match.Success)
        {
            return "DEBUG";
        }
        var level = match.Value;
        return level == "TRACE" ? "DEBUG" : level;
    }

    /// <summary>
    /// 从文件末尾向前扫描，返回最新在前的、命中过滤条件的至多 count 行。
    /// 跨块行以原始字节累积（lineTail），完整行合并后再一次性 UTF-8 解码，
    /// 避免多字节字符在 8KB 块边界被截断产生乱码/破坏关键词匹配。
    /// 扫描上限 MaxScanBytes；文件读取中追加/删除均降级为已读内容。
    /// </summary>
    private static IReadOnlyList<string> ReadTail(string filePath, int count, string? levelFilter, string? keywordFilter)
    {
        var matched = new List<string>();
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return matched;
            }

            var buffer = new byte[ChunkSize];
            long scanPos = stream.Length;
            long scannedBytes = 0L;
            // 跨块未闭合行的原始字节：后读到的块在时间上更早，其字节应拼在行首，故用 InsertRange(0, ...)
            var lineTail = new List<byte>(ChunkSize * 2);

            while (scanPos > 0 && matched.Count < count && scannedBytes < MaxScanBytes)
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

                // 块内从尾向前按 \n 切行：完整行字节 = buffer[i+1..segmentEnd) + lineTail，合并后一次解码
                var segmentEnd = read;
                for (var i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }
                    var headLen = segmentEnd - i - 1;
                    var lineBytes = new byte[headLen + lineTail.Count];
                    Buffer.BlockCopy(buffer, i + 1, lineBytes, 0, headLen);
                    if (lineTail.Count > 0)
                    {
                        lineTail.CopyTo(lineBytes, headLen);
                        lineTail.Clear();
                    }
                    var line = Encoding.UTF8.GetString(lineBytes).TrimEnd('\r');
                    if (Matches(line, levelFilter, keywordFilter))
                    {
                        matched.Add(line);
                        if (matched.Count >= count)
                        {
                            break;
                        }
                    }
                    segmentEnd = i;
                }
                if (matched.Count >= count)
                {
                    break;
                }
                // 块首残留字节（块内首个 \n 之前的部分）与更早的数据相连
                var head = new byte[segmentEnd];
                Buffer.BlockCopy(buffer, 0, head, 0, segmentEnd);
                lineTail.InsertRange(0, head);
            }

            // 扫描到文件头：处理残余的首行（可能不带换行结尾）
            if (matched.Count < count && lineTail.Count > 0)
            {
                var firstLine = Encoding.UTF8.GetString(lineTail.ToArray()).TrimEnd('\r');
                if (firstLine.Length > 0 && Matches(firstLine, levelFilter, keywordFilter))
                {
                    matched.Add(firstLine);
                }
            }
            return matched;
        }
        catch (Exception)
        {
            return matched;
        }
    }
}
