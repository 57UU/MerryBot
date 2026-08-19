using CommonLib;
using LlmBackend;

namespace Agent.Session;

/// <summary>
/// Default executor for the existing AgentSessionManager. The session's
/// default message channel remains owned by the upper layer that creates it.
/// </summary>
public sealed class AgentSessionClockExecutor : IClockExecutor
{
    private readonly AgentSessionManager _sessionManager;
    private readonly Func<string, string, TokenUsage, Task>? _recordAiMessage;
    private readonly ISimpleLogger _logger;

    public AgentSessionClockExecutor(
        AgentSessionManager sessionManager,
        Func<string, string, TokenUsage, Task>? recordAiMessage = null,
        ISimpleLogger? logger = null)
    {
        _sessionManager = sessionManager;
        _recordAiMessage = recordAiMessage;
        _logger = logger ?? SimpleLog.Default;
    }

    public async Task<ClockExecutionResult> ExecuteAsync(
        ClockTask task,
        CancellationToken cancellationToken)
    {
        var session = await _sessionManager.GetSessionAsync(task.SessionId);
        var (response, usage) = await session.ChatAndWaitAsync(task.Content, cancellationToken: cancellationToken);
        if (_recordAiMessage != null)
        {
            try
            {
                await _recordAiMessage(task.SessionId, response, usage);
            }
            catch (Exception ex)
            {
                // 记录失败不影响定时任务执行结果，但需落日志便于排查
                _logger.Warn($"AI 审计记录失败（{task.SessionId}）: {ex.Message}");
            }
        }
        return ClockExecutionResult.Success(response);
    }
}
