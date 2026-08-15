namespace Agent.Session;

public class AgentSessionManager
{
    /// <summary>会话空闲超过该时长即视为可淘汰</summary>
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromHours(12);
    /// <summary>惰性空闲清理的最小间隔，避免每次获取会话都做全表扫描</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly Func<string, Task<(Agent, Action<string> defaultMessageChannel)>> _agentCreator;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<AgentSession>>> _agentSessions = new();
    private long _lastCleanupTicks;

    public AgentSessionManager(Func<string, Task<(Agent, Action<string> defaultMessageChannel)>> agentCreator)
    {
        _agentCreator = agentCreator ?? throw new ArgumentNullException(nameof(agentCreator));
    }

    /// <summary>
    /// Gets a session, creating it asynchronously when needed. Concurrent callers
    /// for the same session share a single creation task.
    /// </summary>
    public async Task<AgentSession> GetSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // 惰性清理：每次获取会话时顺带检查（受 CleanupInterval 节流），避免空闲会话永不淘汰
        TryCleanupIdleSessions();

        var lazySession = _agentSessions.GetOrAdd(
            sessionId,
            static (id, creator) => new Lazy<Task<AgentSession>>(
                async () =>
                {
                    var (agent, defaultMessageChannel) = await creator(id);
                    return new AgentSession(agent, defaultMessageChannel);
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
    /// 惰性空闲清理：距上次清理超过 CleanupInterval 时扫描一次，空闲超过
    /// IdleSessionTimeout 且当前不忙的会话从字典移除（AgentSession/Agent 均无 Dispose，
    /// "释放"即移除引用，交由 GC 回收）。只在会话不忙时清理，避免打断正在处理的消息；
    /// 只移除仍是原 Lazy 实例的条目，避免误删并发新建的会话。
    /// </summary>
    private void TryCleanupIdleSessions()
    {
        var now = DateTime.UtcNow;
        if (Interlocked.Read(ref _lastCleanupTicks) > now.Ticks - CleanupInterval.Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastCleanupTicks, now.Ticks);

        foreach (var kvp in _agentSessions)
        {
            var lazy = kvp.Value;
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }
            var session = lazy.Value.Result;
            // 正在处理消息的会话不清理；LastActiveUtc 由会话在入队/处理时刷新
            if (session.IsBusy || now - session.LastActiveUtc <= IdleSessionTimeout)
            {
                continue;
            }
            ((ICollection<KeyValuePair<string, Lazy<Task<AgentSession>>>>)_agentSessions)
                .Remove(new KeyValuePair<string, Lazy<Task<AgentSession>>>(kvp.Key, lazy));
        }
    }
}
