using System.Collections.Concurrent;
using CommonLib;

namespace Agent.Session;

public class AgentSessionManager : IDisposable
{
    /// <summary>会话空闲超过该时长即视为可淘汰（默认 12 小时，可由配置覆盖）</summary>
    private readonly TimeSpan _idleSessionTimeout;
    /// <summary>后台监控清理的扫描间隔</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly Func<string, Task<(Agent, Action<string> defaultMessageChannel)>> _agentCreator;
    private readonly ISimpleLogger _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<AgentSession>>> _agentSessions = new();
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly Task _cleanupTask;

    public AgentSessionManager(
        Func<string, Task<(Agent, Action<string> defaultMessageChannel)>> agentCreator,
        TimeSpan? idleSessionTimeout = null,
        ISimpleLogger? logger = null)
    {
        _agentCreator = agentCreator ?? throw new ArgumentNullException(nameof(agentCreator));
        _idleSessionTimeout = idleSessionTimeout ?? TimeSpan.FromHours(12);
        _logger = logger ?? SimpleLog.Default;
        if (_idleSessionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleSessionTimeout), "会话空闲淘汰时长必须大于 0。");
        }
        // 后台监控任务：定期扫描并清理空闲会话（清理前先压缩，减少下次加载占用）
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token));
    }

    /// <summary>
    /// Gets a session, creating it asynchronously when needed. Concurrent callers
    /// for the same session share a single creation task.
    /// </summary>
    public async Task<AgentSession> GetSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var lazySession = _agentSessions.GetOrAdd(
            sessionId,
            (id, creator) => new Lazy<Task<AgentSession>>(
                async () =>
                {
                    var (agent, defaultMessageChannel) = await creator(id);
                    return new AgentSession(agent, defaultMessageChannel, _logger);
                },
                LazyThreadSafetyMode.ExecutionAndPublication),
            _agentCreator);

        try
        {
            return await lazySession.Value;
        }
        catch
        {
            // A failed initialization must be retriable.
            ((ICollection<KeyValuePair<string, Lazy<Task<AgentSession>>>>)_agentSessions)
                .Remove(new KeyValuePair<string, Lazy<Task<AgentSession>>>(sessionId, lazySession));
            throw;
        }
    }

    /// <summary>
    /// 移除指定会话并立即重建：重建会重新执行 creator（重新构建 LLM 客户端与工具集）。
    /// 供 /new 使用——调用方需先清空持久化历史，再重建以刷新 tools 并从空历史开始。
    /// </summary>
    public async Task<AgentSession> RebuildSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _agentSessions.TryRemove(sessionId, out _);
        return await GetSessionAsync(sessionId);
    }

    /// <summary>
    /// 指定会话是否正在处理消息。会话不存在或尚未初始化完成时返回 false；
    /// 查询不会创建会话（与 <see cref="GetSessionAsync"/> 不同）。
    /// 供 WebUI 会话控制（清除前判断忙闲）与空闲清理之外的调用方使用。
    /// </summary>
    public bool IsSessionBusy(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return TryGetLiveSession(sessionId, out var session) && session is not null && session.IsBusy;
    }

    /// <summary>
    /// 获取已存在的常驻会话（不创建）。会话不存在、初始化失败或尚未完成时返回 false。
    /// </summary>
    public bool TryGetLiveSession(string sessionId, out AgentSession? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        session = null;
        if (!_agentSessions.TryGetValue(sessionId, out var lazy)
            || !lazy.IsValueCreated
            || !lazy.Value.IsCompletedSuccessfully)
        {
            return false;
        }
        session = lazy.Value.Result;
        return true;
    }

    /// <summary>后台监控循环：按 CleanupInterval 周期扫描空闲会话，Dispose 取消时静默退出。</summary>
    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CleanupInterval, cancellationToken);
                try
                {
                    await CleanupIdleSessionsAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"会话空闲清理失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Dispose 取消）
        }
    }

    /// <summary>
    /// 扫描并清理空闲会话：清理（移除引用，交由 GC）前先调用 CompactAsync 压缩上下文，
    /// 压缩摘要会被持久化，下次重建该会话时从压缩快照恢复，减少占用。
    /// 只在会话不忙时清理，避免打断正在处理的消息；只移除仍是原 Lazy 实例的条目，
    /// 避免误删并发新建的会话。
    /// </summary>
    private async Task CleanupIdleSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _agentSessions)
        {
            var lazy = kvp.Value;
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }
            var session = lazy.Value.Result;
            // 正在处理消息的会话不清理；LastActiveUtc 由会话在入队/处理时刷新
            if (session.IsBusy || now - session.LastActiveUtc <= _idleSessionTimeout)
            {
                continue;
            }

            // 清理前先压缩：压缩摘要持久化后，下次加载直接从压缩快照恢复。
            // 压缩失败（如 LLM 不可用）记录日志仍继续清理——历史已逐轮落库，移除引用不丢数据。
            try
            {
                await session.CompactAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"会话压缩失败（{kvp.Key}），仍按空闲清理: {ex.Message}");
            }

            // 压缩期间可能有新消息入队（刷新 LastActiveUtc 或正在处理），再确认一次仍空闲才移除
            var now2 = DateTime.UtcNow;
            if (session.IsBusy || now2 - session.LastActiveUtc <= _idleSessionTimeout)
            {
                continue;
            }
            ((ICollection<KeyValuePair<string, Lazy<Task<AgentSession>>>>)_agentSessions)
                .Remove(new KeyValuePair<string, Lazy<Task<AgentSession>>>(kvp.Key, lazy));
        }
    }

    public void Dispose()
    {
        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
    }
}
