using System.Text;
using System.Text.RegularExpressions;

namespace MerryBot.WebUI.Api;

public sealed record LogContentDto(string? File, IReadOnlyList<string> Lines);
public sealed record LogFileInfoDto(string Name, DateTimeOffset LastWriteTimeUtc, long SizeBytes, bool IsCurrent);

/// <summary>
/// 日志文件读取：NLog 落盘日志末尾若干行，支持按级别/关键词后端过滤
/// （向后多扫，避免前端过滤漏掉更早的匹配行）与历史日志文件浏览切换。
/// 原 LogApiMapper 的纯逻辑部分（无 HTTP 依赖），供 Blazor 页面直接注入调用。
/// </summary>
public sealed class LogFileService
{
    private const int DefaultLines = 500;
    private const int MinLines = 100;
    private const int MaxLines = 2000;
    private const long MaxScanBytes = 64 * 1024 * 1024;
    private const int ChunkSize = 8192;
    private static readonly Regex LevelRegex = new(@"\b(TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\b", RegexOptions.Compiled);

    private readonly string logDirectory;

    public LogFileService(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.logDirectory = logDirectory;
    }

    /// <summary>
    /// 读取目标日志末尾若干行。
    /// level: 全部|TRACE|DEBUG|INFO|WARN|ERROR|FATAL（缺省/全部 = 不过滤；TRACE 并入 DEBUG 与页面一致）。
    /// keyword: 大小写不敏感子串（缺省空 = 不过滤）。
    /// fileName: 历史日志文件名（缺省 = 当前文件）。
    /// </summary>
    public LogContentDto ReadCurrent(int? lines, string? level, string? keyword, string? fileName)
    {
        int count = Math.Clamp(lines ?? DefaultLines, MinLines, MaxLines);
        string? targetFile = ResolveLogFile(fileName);
        if (targetFile == null)
        {
            return new LogContentDto(null, Array.Empty<string>());
        }

        List<string> logLines = ReadTail(targetFile, count, NormalizeLevel(level), NormalizeKeyword(keyword));
        return new LogContentDto(Path.GetFileName(targetFile), logLines);
    }

    /// <summary>历史日志文件列表（按最后写入时间倒序；IsCurrent 为当前正在写入的文件）。</summary>
    public IReadOnlyList<LogFileInfoDto> ListFiles()
    {
        string? currentName = FindCurrentLogFile() is { } current ? Path.GetFileName(current) : null;
        return ListLogFiles()
            .Select(f => new LogFileInfoDto(
                f.Name,
                new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                f.Length,
                string.Equals(f.Name, currentName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>日志目录下最后写入的 *.log（即当前启动的日志）；目录不存在或为空返回 null。</summary>
    private string? FindCurrentLogFile()
    {
        try
        {
            DirectoryInfo directory = new(logDirectory);
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
    private string? ResolveLogFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FindCurrentLogFile();
        }
        try
        {
            DirectoryInfo directory = new(logDirectory);
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

    private IEnumerable<FileInfo> ListLogFiles()
    {
        try
        {
            DirectoryInfo directory = new(logDirectory);
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
        Match match = LevelRegex.Match(line);
        if (!match.Success)
        {
            return "DEBUG";
        }
        string level = match.Value;
        return level == "TRACE" ? "DEBUG" : level;
    }

    /// <summary>
    /// 从文件末尾向前扫描，返回最新在前的、命中过滤条件的至多 count 行。
    /// 跨块行以原始字节累积（lineTail），完整行合并后再一次性 UTF-8 解码，
    /// 避免多字节字符在 8KB 块边界被截断产生乱码/破坏关键词匹配。
    /// 扫描上限 MaxScanBytes；文件读取中追加/删除均降级为已读内容。
    /// </summary>
    private static List<string> ReadTail(string filePath, int count, string? levelFilter, string? keywordFilter)
    {
        List<string> matched = new();
        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return matched;
            }

            byte[] buffer = new byte[ChunkSize];
            long scanPos = stream.Length;
            long scannedBytes = 0L;
            // 跨块未闭合行的原始字节：后读到的块在时间上更早，其字节应拼在行首，故用 InsertRange(0, ...)
            List<byte> lineTail = new(ChunkSize * 2);

            while (scanPos > 0 && matched.Count < count && scannedBytes < MaxScanBytes)
            {
                int readSize = (int)Math.Min(scanPos, ChunkSize);
                scanPos -= readSize;
                stream.Seek(scanPos, SeekOrigin.Begin);
                int read = stream.Read(buffer, 0, readSize);
                if (read <= 0)
                {
                    break;
                }
                scannedBytes += read;

                // 块内从尾向前按 \n 切行：完整行字节 = buffer[i+1..segmentEnd) + lineTail，合并后一次解码
                int segmentEnd = read;
                for (int i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }
                    int headLen = segmentEnd - i - 1;
                    byte[] lineBytes = new byte[headLen + lineTail.Count];
                    Buffer.BlockCopy(buffer, i + 1, lineBytes, 0, headLen);
                    if (lineTail.Count > 0)
                    {
                        lineTail.CopyTo(lineBytes, headLen);
                        lineTail.Clear();
                    }
                    string line = Encoding.UTF8.GetString(lineBytes).TrimEnd('\r');
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
                byte[] head = new byte[segmentEnd];
                Buffer.BlockCopy(buffer, 0, head, 0, segmentEnd);
                lineTail.InsertRange(0, head);
            }

            // 扫描到文件头：处理残余的首行（可能不带换行结尾）
            if (matched.Count < count && lineTail.Count > 0)
            {
                string firstLine = Encoding.UTF8.GetString(lineTail.ToArray()).TrimEnd('\r');
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
