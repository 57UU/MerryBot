namespace Agent.Session;

public class AgentSessionManager
{
    private readonly Func<string, Task<(Agent, Action<string> defaultMessageChannel)>> _agentCreator;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<AgentSession>>> _agentSessions = new();

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
}
