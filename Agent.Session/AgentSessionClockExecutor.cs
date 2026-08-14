namespace Agent.Session;

/// <summary>
/// Default executor for the existing AgentSessionManager. The session's
/// default message channel remains owned by the upper layer that creates it.
/// </summary>
public sealed class AgentSessionClockExecutor : IClockExecutor
{
    private readonly AgentSessionManager _sessionManager;

    public AgentSessionClockExecutor(AgentSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public async Task<ClockExecutionResult> ExecuteAsync(
        ClockTask task,
        CancellationToken cancellationToken)
    {
        var session = await _sessionManager.GetSessionAsync(task.SessionId);
        var response = await session.ChatAndWaitAsync(task.Content, cancellationToken: cancellationToken);
        return ClockExecutionResult.Success(response);
    }
}
