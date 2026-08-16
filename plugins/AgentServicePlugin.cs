using Agent.Tools;
using CommonLib;

namespace BotPlugin;

/// <summary>
/// Agent 服务插件：对外注册 Skill / 记忆管理接口（供运行时工具与 WebUI 使用），
/// 内部持有具体服务实现并转发调用，同时暴露给 AgentPlugin 复用同一份服务实例。
/// </summary>
[PluginTag("agent-service", "Agent服务", "向运行时与 WebUI 提供 Skill 与记忆管理服务")]
public sealed class AgentServicePlugin : Plugin, ISkillManagementService, IMemoryManagementService
{
    private readonly FileSkillManagementService skillService;
    private readonly MemoryManagementService memoryService;

    public AgentServicePlugin(PluginInterop interop) : base(interop)
    {
        skillService = new FileSkillManagementService(Path.Combine(Interop.PathPrefix, "skills"));
        memoryService = new MemoryManagementService(Interop.PluginStorage.PluginDatabaseScope);
    }

    /// <summary>供 AgentPlugin 复用：Skill 文件存储服务。</summary>
    internal FileSkillManagementService SkillService => skillService;

    /// <summary>供 AgentPlugin 复用：数据库记忆服务（含 MemoryToolSet 创建）。</summary>
    internal MemoryManagementService MemoryService => memoryService;

    // ── ISkillManagementService 转发 ─────────────────────────────────────────

    public Task<IReadOnlyList<ManagedSkill>> ListSkillsAsync(CancellationToken cancellationToken = default)
        => skillService.ListSkillsAsync(cancellationToken);

    public Task<string> ReadSkillAsync(string name, bool includeDisabled = false, CancellationToken cancellationToken = default)
        => skillService.ReadSkillAsync(name, includeDisabled, cancellationToken);

    public Task UploadSkillAsync(SkillUpload upload, CancellationToken cancellationToken = default)
        => skillService.UploadSkillAsync(upload, cancellationToken);

    public Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
        => skillService.SetSkillEnabledAsync(name, enabled, cancellationToken);

    public Task DeleteSkillAsync(string name, CancellationToken cancellationToken = default)
        => skillService.DeleteSkillAsync(name, cancellationToken);

    // ── IMemoryManagementService 转发 ────────────────────────────────────────

    public Task<IReadOnlyList<ManagedMemorySession>> ListMemorySessionsAsync(CancellationToken cancellationToken = default)
        => memoryService.ListMemorySessionsAsync(cancellationToken);

    public Task<string> GetMemoryIndexAsync(string sessionKey, CancellationToken cancellationToken = default)
        => memoryService.GetMemoryIndexAsync(sessionKey, cancellationToken);

    public Task SaveMemoryIndexAsync(string sessionKey, string content, CancellationToken cancellationToken = default)
        => memoryService.SaveMemoryIndexAsync(sessionKey, content, cancellationToken);

    public Task<IReadOnlyList<ManagedMemory>> ListMemoriesAsync(string sessionKey, CancellationToken cancellationToken = default)
        => memoryService.ListMemoriesAsync(sessionKey, cancellationToken);

    public Task<ManagedMemory?> GetMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken = default)
        => memoryService.GetMemoryAsync(sessionKey, key, cancellationToken);

    public Task SaveMemoryAsync(string sessionKey, string key, string content, CancellationToken cancellationToken = default)
        => memoryService.SaveMemoryAsync(sessionKey, key, content, cancellationToken);

    public Task<bool> DeleteMemoryAsync(string sessionKey, string key, CancellationToken cancellationToken = default)
        => memoryService.DeleteMemoryAsync(sessionKey, key, cancellationToken);

    public Task<string?> GetPromptInjectionAsync(string sessionKey, CancellationToken cancellationToken = default)
        => memoryService.GetPromptInjectionAsync(sessionKey, cancellationToken);
}
