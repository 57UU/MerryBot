namespace CommonLib;

/// <summary>供运行时工具和 WebUI 共用的、按 SessionKey 隔离的记忆管理能力。</summary>
public interface IMemoryManagementService
{
    Task<IReadOnlyList<ManagedMemorySession>> ListMemorySessionsAsync(CancellationToken cancellationToken = default);
    Task<string> GetMemoryIndexAsync(string sessionKey, CancellationToken cancellationToken = default);
    Task SaveMemoryIndexAsync(string sessionKey, string content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagedMemory>> ListMemoriesAsync(string sessionKey, CancellationToken cancellationToken = default);
    Task<ManagedMemory?> GetMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken = default);
    Task SaveMemoryAsync(string sessionKey, string key, string content, CancellationToken cancellationToken = default);
    Task<bool> DeleteMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken = default);
    Task<string?> GetPromptInjectionAsync(string sessionKey, CancellationToken cancellationToken = default);
}

public sealed record ManagedMemorySession(string SessionKey, DateTimeOffset UpdatedAtUtc);
public sealed record ManagedMemory(string Key, string Content, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
