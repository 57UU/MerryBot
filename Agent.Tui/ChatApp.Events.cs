using System.Text;
using Agent.Tui.Core;
using LlmBackend;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    /// <summary>
    /// 供 Program 的 AgentOptions.OnLog 回调使用(任意线程可调,内部仅操作状态 + Invalidate)。
    /// tool 调用与模型中间输出始终显示;其余事件仅在 /debug 开启时显示。
    /// </summary>
    public void OnAgentLog(AgentLogEvent eventInfo)
    {
        switch (eventInfo.Kind)
        {
            case AgentLogEventKind.ToolCallStarted:
            case AgentLogEventKind.ToolCallCompleted:
            case AgentLogEventKind.ToolCallFailed:
            case AgentLogEventKind.ModelRequest:
            case AgentLogEventKind.ModelResponse:
                AppendProcess(eventInfo);
                break;
            case AgentLogEventKind.ModelTextDelta:
                AppendStreamingDelta(eventInfo);
                return; // 高频事件:不进 debug 日志(否则每 token 一行刷屏)
            case AgentLogEventKind.ModelReasoningDelta:
                return; // 推理增量不渲染
            case AgentLogEventKind.ModelStreamSegmentStart:
                break;
            case AgentLogEventKind.ModelStreamSegmentReset:
                DiscardStreamingRows();
                break;
            case AgentLogEventKind.ChatStarted:
                ShowThinking();
                break;
            case AgentLogEventKind.ChatCompleted:
                FinalizeStreaming(eventInfo);
                ClearPane();
                break;
            case AgentLogEventKind.ChatFailed:
                ResetStreamingState();
                _pendingModelContent = null;
                ClearPane();
                break;
        }
        AppendDebug(FormatLogEvent(eventInfo));
    }

    /// <summary>渲染对话过程:模型中间输出暂存 + 工具调用状态行。</summary>
    private void AppendProcess(AgentLogEvent eventInfo)
    {
        switch (eventInfo.Kind)
        {
            case AgentLogEventKind.ModelResponse:
                _pendingModelContent = string.IsNullOrWhiteSpace(eventInfo.Result) ? null : eventInfo.Result;
                break;
            case AgentLogEventKind.ModelRequest:
                _pendingModelContent = null;
                break;
            case AgentLogEventKind.ToolCallStarted:
                FlushStreamingToPane();
                FlushPendingModel();
                AppendToolStatus(eventInfo.ToolCallId, $"● tool: {ToolName(eventInfo)} 执行中…");
                break;
            case AgentLogEventKind.ToolCallCompleted:
                AppendToolStatus(eventInfo.ToolCallId, $"● tool: {ToolName(eventInfo)} 已完成");
                if (!string.IsNullOrWhiteSpace(eventInfo.Result))
                {
                    AppendLine(ChatRole.Tool, new string(' ', 3) + Truncate(eventInfo.Result, 80));
                }
                break;
            case AgentLogEventKind.ToolCallFailed:
                AppendToolStatus(eventInfo.ToolCallId, $"● tool: {ToolName(eventInfo)} 失败: {Truncate(eventInfo.Exception?.Message ?? eventInfo.Result, 80)}");
                break;
        }
    }

    /// <summary>工具状态行:有 ToolCallId 记录时就地更新,否则追加新行。</summary>
    private readonly Dictionary<string, int> _toolLines = [];

    private void AppendToolStatus(string? toolCallId, string text)
    {
        var colored = RoleColorApply(ChatRole.Tool, text);
        if (toolCallId is not null && _toolLines.TryGetValue(toolCallId, out var idx)
            && idx >= 0 && idx < _chat.LineCount)
        {
            _chat.SetLine(idx, colored);
        }
        else
        {
            _chat.Append(colored);
            if (toolCallId is not null)
            {
                _toolLines[toolCallId] = _chat.LineCount - 1;
            }
        }
        Invalidate();
    }

    private static string ToolName(AgentLogEvent eventInfo) =>
        string.IsNullOrWhiteSpace(eventInfo.ToolName) ? "unknown" : eventInfo.ToolName;

    private void ShowThinking()
    {
        SetPaneLine("· Agent is Thinking…");
        Invalidate();
    }

    /// <summary>把确认是中间轮次的模型输出累积进思考面板(单行滚动展示最近一段)。</summary>
    private void FlushPendingModel()
    {
        if (_pendingModelContent is null) return;
        var content = _pendingModelContent;
        _pendingModelContent = null;
        if (_paneText.Length > 0) _paneText.Append('\n');
        _paneText.Append(content.Replace("\r", string.Empty));
        SetPaneLine(_paneText.ToString());
    }

    /// <summary>清空思考面板并清理工具状态行索引。</summary>
    private void ClearPane()
    {
        _toolLines.Clear();
        _paneText.Clear();
        _paneLine = string.Empty;
        Invalidate();
    }

    /// <summary>
    /// 模型文本增量:追加到流式缓冲,请求一次重绘(渲染循环每帧调用 FlushStreamingToChat 刷入聊天区)。
    /// </summary>
    private void AppendStreamingDelta(AgentLogEvent eventInfo)
    {
        var delta = eventInfo.Result;
        if (string.IsNullOrEmpty(delta)) return;
        lock (_streamSync)
        {
            (_streamingBuffer ??= new StringBuilder()).Append(delta);
        }
        Invalidate();
    }

    /// <summary>渲染前调用:把累积的流式增量刷入聊天区(加锁,与 Agent 线程安全协作)。</summary>
    private void FlushStreamingToChat()
    {
        lock (_streamSync)
        {
            if (_streamingBuffer is not { Length: > 0 }) return;
            RefreshStreamingRowLocked();
        }
    }

    /// <summary>把累积的流式内容重写为聊天区末尾的 Assistant 行区间(加锁入口)。</summary>
    private void RefreshStreamingRow()
    {
        lock (_streamSync)
        {
            if (_streamingBuffer is not { Length: > 0 }) return;
            RefreshStreamingRowLocked();
        }
    }

    /// <summary>流式重写实现:调用方须已持有 _streamSync 锁。</summary>
    private void RefreshStreamingRowLocked()
    {
        var text = _streamingBuffer!.ToString();
        if (_streamLineStart < 0)
        {
            _streamLineStart = _chat.LineCount;
        }

        var lines = text.Replace("\r", string.Empty).Split('\n');
        var prefix = RolePrefix("Assistant");
        var indent = new string(' ', TextWidth.Measure(prefix));
        // 流式行不重复着色:行已含样式前缀,先去掉原前缀再组装
        int existing = _chat.LineCount - _streamLineStart;
        int common = Math.Min(existing, lines.Length);
        for (int i = 0; i < common; i++)
        {
            _chat.SetLine(_streamLineStart + i, RoleColorApply(ChatRole.Assistant, i == 0 ? prefix + lines[i] : indent + lines[i]));
        }
        if (lines.Length < existing)
        {
            _chat.TruncateFrom(_streamLineStart + lines.Length);
        }
        else
        {
            for (int i = common; i < lines.Length; i++)
            {
                _chat.Append(RoleColorApply(ChatRole.Assistant, i == 0 ? prefix + lines[i] : indent + lines[i]));
            }
        }
    }

    /// <summary>中间轮(工具调用)流式内容收尾:把已实时显示的输出撤下,追加进思考面板。</summary>
    private void FlushStreamingToPane()
    {
        string? content;
        int lineStart;
        lock (_streamSync)
        {
            content = _streamingBuffer is { Length: > 0 } ? _streamingBuffer.ToString() : null;
            _streamingBuffer = null;
        }
        lineStart = _streamLineStart;
        _streamLineStart = -1;
        if (content is null) return;
        if (lineStart >= 0 && lineStart < _chat.LineCount)
        {
            _chat.TruncateFrom(lineStart);
        }
        if (_paneText.Length > 0) _paneText.Append('\n');
        _paneText.Append(content.Replace("\r", string.Empty));
        SetPaneLine(_paneText.ToString());
    }

    /// <summary>最终回复定稿:有流式缓冲就做最后一次重写,否则用 ChatCompleted 结果兜底。</summary>
    private void FinalizeStreaming(AgentLogEvent eventInfo)
    {
        lock (_streamSync)
        {
            if (_streamingBuffer is { Length: > 0 })
            {
                RefreshStreamingRowLocked();
            }
        }
        if (_streamingBuffer is null && !string.IsNullOrWhiteSpace(eventInfo.Result))
        {
            AppendChat("Assistant", eventInfo.Result);
        }
        ResetStreamingState();
    }

    /// <summary>丢弃流式状态。</summary>
    private void ResetStreamingState()
    {
        lock (_streamSync)
        {
            _streamingBuffer = null;
        }
        _streamLineStart = -1;
    }

    /// <summary>流式 segment 作废(模型重连重试):撤下已渲染的半成品行并丢弃缓冲。</summary>
    private void DiscardStreamingRows()
    {
        int lineStart;
        lock (_streamSync)
        {
            _streamingBuffer = null;
            lineStart = _streamLineStart;
        }
        _streamLineStart = -1;
        if (lineStart >= 0 && lineStart < _chat.LineCount)
        {
            _chat.TruncateFrom(lineStart);
        }
        Invalidate();
    }

    private static string FormatLogEvent(AgentLogEvent eventInfo)
    {
        var time = eventInfo.TimestampUtc.ToString("HH:mm:ss.fff'Z'");
        var iteration = eventInfo.Iteration > 0 ? $" iteration={eventInfo.Iteration}" : string.Empty;
        return eventInfo.Kind switch
        {
            AgentLogEventKind.ToolCallStarted =>
                $"[{time}] [tool.start]{iteration} {ToolLabel(eventInfo)} args={Truncate(eventInfo.Arguments)}",
            AgentLogEventKind.ToolCallCompleted =>
                $"[{time}] [tool.result]{iteration} {ToolLabel(eventInfo)} result={Truncate(eventInfo.Result)}",
            AgentLogEventKind.ToolCallFailed =>
                $"[{time}] [tool.error]{iteration} {ToolLabel(eventInfo)} {Truncate(eventInfo.Exception?.Message ?? eventInfo.Result)}",
            AgentLogEventKind.ModelRequest =>
                $"[{time}] [agent.model.request]{iteration}",
            AgentLogEventKind.ModelStreamSegmentStart =>
                $"[{time}] [agent.model.segment]{iteration} start attempt={eventInfo.Result}",
            AgentLogEventKind.ModelStreamSegmentReset =>
                $"[{time}] [agent.model.segment]{iteration} reset reason={eventInfo.Result} {Truncate(eventInfo.Exception?.Message)}",
            AgentLogEventKind.ModelResponse =>
                $"[{time}] [agent.model.response]{iteration} {FormatUsage(eventInfo.Usage)} content={Truncate(eventInfo.Result)}",
            AgentLogEventKind.ContextCompaction =>
                $"[{time}] [agent.context]{iteration} {eventInfo.Result ?? eventInfo.Exception?.Message ?? "failed"}",
            AgentLogEventKind.ChatStarted =>
                $"[{time}] [agent.chat] started",
            AgentLogEventKind.ChatCompleted =>
                $"[{time}] [agent.chat] completed {FormatUsage(eventInfo.Usage)}",
            AgentLogEventKind.ChatFailed =>
                $"[{time}] [agent.chat.error] {Truncate(eventInfo.Exception?.Message)}",
            _ => $"[{time}] [agent] {eventInfo.Kind}",
        };
    }

    private static string ToolLabel(AgentLogEvent eventInfo) =>
        string.IsNullOrWhiteSpace(eventInfo.ToolCallId)
            ? eventInfo.ToolName ?? "unknown"
            : $"{eventInfo.ToolName ?? "unknown"} id={eventInfo.ToolCallId}";

    private static string FormatUsage(TokenUsage? usage) => usage == null
        ? string.Empty
        : $"usage={usage.totalUsage} (input={usage.promptUsage}, output={usage.completionUsage}, cached={usage.cachedUsage})";

    private static string Truncate(string? value, int maximumLength = 1000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }
        var normalized = value.Replace("\r", string.Empty).Replace("\n", "\\n");
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + $"… ({normalized.Length} chars)";
    }
}