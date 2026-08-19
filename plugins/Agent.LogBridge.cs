using Agent;
using CommonLib;

namespace BotPlugin;

/// <summary>
/// 把 Agent 引擎的 <see cref="AgentLogEvent"/> 事件流桥接到插件日志（ISimpleLogger → NLog）。
/// 高频增量事件（ModelTextDelta/ModelReasoningDelta）落 Trace，经 NLog rule（Debug 起）静默丢弃，不刷屏；
/// 生命周期事件（会话/工具调用/压缩）按严重程度映射到 Info/Warn/Error。
/// </summary>
public static class AgentLogBridge
{
    public static void Log(AgentLogEvent e, ISimpleLogger log)
    {
        switch (e.Kind)
        {
            case AgentLogEventKind.ChatStarted:
                log.Info($"Agent 会话开始（Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ChatCompleted:
                log.Info($"Agent 会话完成（Iteration={e.Iteration}，结果长度={e.Result?.Length ?? 0}）");
                break;
            case AgentLogEventKind.ChatFailed:
                if (e.Exception == null)
                {
                    log.Warn($"Agent 会话失败（Iteration={e.Iteration}）");
                }
                else
                {
                    log.Error(e.Exception, $"Agent 会话失败（Iteration={e.Iteration}）");
                }
                break;
            case AgentLogEventKind.ModelRequest:
                log.Debug($"模型请求（Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ModelResponse:
                log.Debug($"模型响应（Iteration={e.Iteration}，usage={e.Usage}）");
                break;
            case AgentLogEventKind.ModelTextDelta:
            case AgentLogEventKind.ModelReasoningDelta:
                // 高频增量：Trace 级别，NLog rule（Debug 起）丢弃，不写文件
                log.Trace($"模型增量（Kind={e.Kind}，Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ModelStreamSegmentStart:
                log.Debug($"模型流式段开始（Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ModelStreamSegmentReset:
                if (e.Exception == null)
                {
                    log.Warn($"模型流式段重置（Iteration={e.Iteration}），将重建流重试");
                }
                else
                {
                    log.Warn(e.Exception, $"模型流式段重置（Iteration={e.Iteration}），将重建流重试");
                }
                break;
            case AgentLogEventKind.ContextCompaction:
                LogContextCompaction(e, log);
                break;
            case AgentLogEventKind.ToolCallStarted:
                log.Info($"工具调用开始 {e.ToolName}（Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ToolCallCompleted:
                log.Debug($"工具调用完成 {e.ToolName}（Iteration={e.Iteration}）");
                break;
            case AgentLogEventKind.ToolCallFailed:
                if (e.Exception == null)
                {
                    log.Error($"工具调用失败 {e.ToolName}（Iteration={e.Iteration}）");
                }
                else
                {
                    log.Error(e.Exception, $"工具调用失败 {e.ToolName}（Iteration={e.Iteration}）");
                }
                break;
        }
    }

    private static void LogContextCompaction(AgentLogEvent e, ISimpleLogger log)
    {
        if (e.Exception != null)
        {
            log.Error(e.Exception, $"上下文压缩失败（Iteration={e.Iteration}）");
            return;
        }
        // 现有 Agent 实现以 Result 承载阶段标记（started/completed）；
        // 未来若改为承载摘要文本，空摘要在此记为 Warn
        if (e.Result == "completed")
        {
            log.Info($"上下文压缩完成（Iteration={e.Iteration}）");
        }
        else if (string.IsNullOrWhiteSpace(e.Result))
        {
            log.Warn($"上下文压缩未产生摘要（Iteration={e.Iteration}），保留原上下文");
        }
        else
        {
            log.Debug($"上下文压缩（Iteration={e.Iteration}，{e.Result}）");
        }
    }
}
