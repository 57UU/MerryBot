namespace MerryBot.Contracts;

/// <summary>供运行时工具和 WebUI 共用的 Skill 管理能力。</summary>
public interface ISkillManagementService
{
    Task<IReadOnlyList<ManagedSkill>> ListSkillsAsync(CancellationToken cancellationToken = default);
    Task<string> ReadSkillAsync(string name, bool includeDisabled = false, CancellationToken cancellationToken = default);
    Task UploadSkillAsync(SkillUpload upload, CancellationToken cancellationToken = default);
    Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);
    Task DeleteSkillAsync(string name, CancellationToken cancellationToken = default);
    Task CloneGitSkillAsync(string gitUrl, string? name = null, CancellationToken cancellationToken = default);
    Task UpdateGitSkillAsync(string name, CancellationToken cancellationToken = default);
}

public sealed record ManagedSkill(
    string Name,
    string? Description,
    bool Enabled,
    SkillLayout Layout,
    long SizeBytes,
    DateTimeOffset UpdatedAtUtc,
    string? GitUrl = null,
    string? GitHead = null);

public enum SkillLayout
{
    MarkdownFile,
    Directory,
    GitRepository,
}

public sealed record SkillUpload(string FileName, byte[] Content);
