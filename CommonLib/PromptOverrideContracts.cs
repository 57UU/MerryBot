namespace CommonLib;

/// <summary>供运行时 Agent 与 WebUI 共用的、按 SessionKey 隔离的系统提示词复写能力。
/// 未复写的会话回退全局 <c>AgentConfig.AiPrompt</c>。</summary>
public interface IPromptOverrideService
{
    /// <summary>提示词复写最大长度（字符），防止超长内容撑爆 system prompt。</summary>
    const int MaxPromptLength = 8000;

    Task<IReadOnlyList<PromptOverrideSession>> ListOverridesAsync(CancellationToken cancellationToken = default);
    Task<PromptOverrideEntry?> GetOverrideAsync(string sessionKey, CancellationToken cancellationToken = default);
    Task SaveOverrideAsync(string sessionKey, string prompt, CancellationToken cancellationToken = default);
    Task<bool> DeleteOverrideAsync(string sessionKey, CancellationToken cancellationToken = default);
}

public sealed record PromptOverrideSession(string SessionKey, DateTimeOffset UpdatedAtUtc);

public sealed record PromptOverrideEntry(string SessionKey, string Prompt, DateTimeOffset UpdatedAtUtc);
