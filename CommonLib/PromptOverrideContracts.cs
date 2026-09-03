namespace CommonLib;

/// <summary>供运行时与 WebUI 共用的、按 SessionKey 隔离的群提示词 override 管理能力。
/// override 为空或全空白时视为删除（该群回退全局提示词）。</summary>
public interface IPromptOverrideService
{
    Task<IReadOnlyList<ManagedPromptOverride>> ListOverridesAsync(CancellationToken cancellationToken = default);
    Task<string> GetOverrideAsync(string sessionKey, CancellationToken cancellationToken = default);
    Task SaveOverrideAsync(string sessionKey, string content, CancellationToken cancellationToken = default);
    Task<bool> DeleteOverrideAsync(string sessionKey, CancellationToken cancellationToken = default);
}

public sealed record ManagedPromptOverride(string SessionKey, string Content, DateTimeOffset UpdatedAtUtc);
