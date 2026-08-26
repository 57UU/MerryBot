namespace Agent.Session;

/// <summary>
/// 绑定插件 Id 的调度器门面：插件经 <c>PluginInterop.Clock</c> 获得，
/// CRUD 与日志查询自动限定在本插件的任务内（与 PluginStorage 的 scope 隔离模式一致）。
/// 执行器注册同样绑定本插件，无需（也无法）感知其他插件的任务。
/// </summary>
public sealed class ClockScope
{
    private readonly ClockService _service;

    public ClockScope(ClockService service, string pluginId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("pluginId 不能为空", nameof(pluginId));
        }
        PluginId = pluginId;
    }

    /// <summary>本门面绑定的插件 Id。</summary>
    public string PluginId { get; }

    /// <summary>
    /// 注册本插件的定时任务执行器；返回被覆盖的旧执行器（无则 null）。
    /// 通常在插件构造函数中调用一次。
    /// </summary>
    public IClockExecutor? RegisterExecutor(IClockExecutor executor)
    {
        return _service.RegisterExecutor(PluginId, executor);
    }

    public Task<ClockTask> CreateAsync(
        string sessionId,
        ClockCreateRequest request,
        CancellationToken cancellationToken = default)
        => _service.CreateAsync(PluginId, sessionId, request, cancellationToken);

    public Task<IReadOnlyList<ClockTask>> ListAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => _service.ListAsync(PluginId, sessionId, cancellationToken);

    public Task<ClockTask> GetAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default)
        => _service.GetAsync(PluginId, sessionId, taskId, cancellationToken);

    public Task<ClockTask> UpdateAsync(
        string sessionId,
        Guid taskId,
        ClockUpdateRequest request,
        CancellationToken cancellationToken = default)
        => _service.UpdateAsync(PluginId, sessionId, taskId, request, cancellationToken);

    public Task DeleteAsync(
        string sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default)
        => _service.DeleteAsync(PluginId, sessionId, taskId, cancellationToken);

    public Task<IReadOnlyList<ClockRunLog>> QueryLogsAsync(
        string sessionId,
        ClockLogQuery query,
        CancellationToken cancellationToken = default)
        => _service.QueryLogsAsync(PluginId, sessionId, query, cancellationToken);
}
