using CommonLib;
using System.IO.Compression;

namespace Agent.Tools;

/// <summary>基于机器人技能目录的 Skill 存储，同时供运行时和 WebUI 管理接口使用。</summary>
public sealed class FileSkillManagementService : ISkillManagementService
{
    private const int MaxZipEntries = 2_000;
    private const long MaxUploadBytes = 20 * 1024 * 1024;
    private readonly string skillsPath;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public FileSkillManagementService(string skillsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsPath);
        this.skillsPath = Path.GetFullPath(skillsPath);
        Directory.CreateDirectory(this.skillsPath);
    }

    public async Task<IReadOnlyList<ManagedSkill>> ListSkillsAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            return ScanSkills().Values
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(skill => skill.ToDto())
                .ToList();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<string> ReadSkillAsync(string name, bool includeDisabled = false, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var skill = GetSkill(ScanSkills(), name);
            if (!includeDisabled && !skill.Enabled)
            {
                throw new InvalidOperationException($"技能已禁用: {skill.Name}");
            }
            return await File.ReadAllTextAsync(skill.EntryPath, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task UploadSkillAsync(SkillUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(upload.FileName);
        ArgumentNullException.ThrowIfNull(upload.Content);
        if (upload.Content.LongLength > MaxUploadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(upload), "Skill 上传文件不能超过 20 MB。");
        }

        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var extension = Path.GetExtension(upload.FileName);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                await UploadMarkdownAsync(upload, cancellationToken);
                return;
            }
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await UploadZipAsync(upload, cancellationToken);
                return;
            }
            throw new ArgumentException("Skill 只支持 .md 文件或目录型 .zip 压缩包。", nameof(upload));
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var skill = GetSkill(ScanSkills(), name);
            if (skill.Enabled == enabled)
            {
                return;
            }
            var targetPath = enabled
                ? skill.EntryPath[..^".disable".Length]
                : skill.EntryPath + ".disable";
            if (File.Exists(targetPath))
            {
                throw new InvalidOperationException($"目标 Skill 文件已存在: {skill.Name}");
            }
            File.Move(skill.EntryPath, targetPath);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task DeleteSkillAsync(string name, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var skill = GetSkill(ScanSkills(), name);
            if (skill.Layout == SkillLayout.MarkdownFile)
            {
                File.Delete(skill.EntryPath);
                return;
            }

            var directory = Path.GetDirectoryName(skill.EntryPath)
                ?? throw new InvalidOperationException("无法确定 Skill 目录。");
            EnsureWithinSkillsPath(directory);
            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task UploadMarkdownAsync(SkillUpload upload, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(upload.FileName);
        if (!fileName.Equals(upload.FileName, StringComparison.Ordinal) || fileName.EndsWith(".disable", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Skill Markdown 文件名无效。", nameof(upload));
        }
        var name = Path.GetFileNameWithoutExtension(fileName);
        ValidateSkillName(name);
        EnsureSkillDoesNotExist(name);

        var destination = Path.Combine(skillsPath, fileName);
        var temporary = destination + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, upload.Content, cancellationToken);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task UploadZipAsync(SkillUpload upload, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(upload.FileName);
        if (!archiveName.Equals(upload.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Skill ZIP 文件名无效。", nameof(upload));
        }

        using var content = new MemoryStream(upload.Content, writable: false);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxZipEntries)
        {
            throw new ArgumentException("Skill ZIP 的文件数量无效。", nameof(upload));
        }

        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        ValidateZipEntries(entries);
        var skillEntries = entries
            .Where(entry => entry.FullName.Replace('\\', '/').EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (skillEntries.Count != 1)
        {
            throw new ArgumentException("目录型 Skill ZIP 必须且只能包含一个 SKILL.md。", nameof(upload));
        }

        var skillEntry = skillEntries[0];
        var normalizedPath = skillEntry.FullName.Replace('\\', '/').Trim('/');
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string name;
        string? sourcePrefix;
        if (parts.Length == 1)
        {
            name = Path.GetFileNameWithoutExtension(archiveName);
            sourcePrefix = null;
        }
        else if (parts.Length == 2)
        {
            name = parts[0];
            sourcePrefix = parts[0] + "/";
            if (entries.Any(entry => !entry.FullName.Replace('\\', '/').StartsWith(sourcePrefix, StringComparison.Ordinal)))
            {
                throw new ArgumentException("目录型 Skill ZIP 的内容必须全部位于同一个顶层目录。", nameof(upload));
            }
        }
        else
        {
            throw new ArgumentException("SKILL.md 必须位于 ZIP 根目录或单个顶层 Skill 目录中。", nameof(upload));
        }

        ValidateSkillName(name);
        EnsureSkillDoesNotExist(name);
        var temporaryDirectory = Path.Combine(skillsPath, ".upload-" + Guid.NewGuid().ToString("N"));
        var destinationDirectory = Path.Combine(skillsPath, name);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            long extractedBytes = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryPath = entry.FullName.Replace('\\', '/');
                var relativePath = sourcePrefix is null ? entryPath : entryPath[sourcePrefix.Length..];
                var outputPath = Path.GetFullPath(Path.Combine(temporaryDirectory, relativePath));
                EnsureWithinDirectory(outputPath, temporaryDirectory);
                var directory = Path.GetDirectoryName(outputPath)!;
                Directory.CreateDirectory(directory);
                await using var input = entry.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
                extractedBytes += entry.Length;
                if (extractedBytes > MaxUploadBytes)
                {
                    throw new ArgumentOutOfRangeException(nameof(upload), "Skill ZIP 解压后不能超过 20 MB。");
                }
            }
            Directory.Move(temporaryDirectory, destinationDirectory);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private Dictionary<string, StoredSkill> ScanSkills()
    {
        var result = new Dictionary<string, StoredSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(skillsPath, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            result[name] = StoredSkill.ForFile(name, file, enabled: true);
        }
        foreach (var file in Directory.EnumerateFiles(skillsPath, "*.md.disable", SearchOption.TopDirectoryOnly))
        {
            var enabledPath = file[..^".disable".Length];
            var name = Path.GetFileNameWithoutExtension(enabledPath);
            result[name] = StoredSkill.ForFile(name, file, enabled: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(skillsPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            var enabledEntry = Path.Combine(directory, "SKILL.md");
            var disabledEntry = enabledEntry + ".disable";
            if (File.Exists(enabledEntry))
            {
                result[name] = StoredSkill.ForDirectory(name, enabledEntry, enabled: true);
            }
            else if (File.Exists(disabledEntry))
            {
                result[name] = StoredSkill.ForDirectory(name, disabledEntry, enabled: false);
            }
        }
        return result;
    }

    private void EnsureSkillDoesNotExist(string name)
    {
        if (ScanSkills().ContainsKey(name))
        {
            throw new InvalidOperationException($"同名 Skill 已存在: {name}");
        }
    }

    private static StoredSkill GetSkill(IReadOnlyDictionary<string, StoredSkill> skills, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return skills.GetValueOrDefault(name.Trim())
            ?? throw new KeyNotFoundException($"未找到 Skill: {name}");
    }

    private static void ValidateZipEntries(IEnumerable<ZipArchiveEntry> entries)
    {
        foreach (var entry in entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(path)
                || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            {
                throw new ArgumentException("Skill ZIP 含有不安全的文件路径。");
            }
        }
    }

    private static void ValidateSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 120
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or "..")
        {
            throw new ArgumentException("Skill 名称无效。", nameof(name));
        }
    }

    private void EnsureWithinSkillsPath(string path) => EnsureWithinDirectory(path, skillsPath);

    private static void EnsureWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Skill 文件路径超出技能目录。");
        }
    }

    private sealed record StoredSkill(string Name, string EntryPath, bool Enabled, SkillLayout Layout)
    {
        public static StoredSkill ForFile(string name, string entryPath, bool enabled)
            => new(name, entryPath, enabled, SkillLayout.MarkdownFile);

        public static StoredSkill ForDirectory(string name, string entryPath, bool enabled)
            => new(name, entryPath, enabled, SkillLayout.Directory);

        public ManagedSkill ToDto()
        {
            var info = new FileInfo(EntryPath);
            var size = Layout == SkillLayout.MarkdownFile
                ? info.Length
                : Directory.EnumerateFiles(Path.GetDirectoryName(EntryPath)!, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
            return new ManagedSkill(Name, Enabled, Layout, size, info.LastWriteTimeUtc);
        }
    }
}
