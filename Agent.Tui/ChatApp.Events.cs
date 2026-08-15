using System.Text;
using LlmBackend;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

#pragma warning disable CS0618 // TextView 在 2.4.17 中标记过时（建议换 tui-cs/Editor），但仍是当前唯一可用的滚动文本视图

namespace Agent.Tui;

public sealed partial class ChatApp
{
    /// <summary>
    /// 供 Program 的 AgentOptions.OnLog 回调使用（已在主线程或后台均安全）。
    /// tool 调用与模型中间输出始终显示；其余事件仅在 /debug 开启时显示。
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
                return;
            case AgentLogEventKind.ModelTextDelta:
                AppendStreamingDelta(eventInfo);
                return;
            case AgentLogEventKind.ChatStarted:
                ShowThinking(); // 响应期间显示 Agent is Thinking
                break;
            case AgentLogEventKind.ChatCompleted:
                // 最终回复已由流式增量就地显示（定稿），无增量时用结果兜底写入
                FinalizeStreaming(eventInfo);
                ClearPane();
                break;
            case AgentLogEventKind.ChatFailed:
                // 丢弃未完成的流式半成品；错误信息由 ChatAndWaitAsync 的调用方展示
                ResetStreamingState();
                _pendingModelContent = null;
                ClearPane();
                break;
        }
        AppendDebug(FormatLogEvent(eventInfo));
    }

    /// <summary>
    /// 渲染对话过程：模型中间输出暂存（确认是中间轮次后写入思考面板）+ 工具调用状态行。
    /// 思路：ModelResponse 先暂存内容，直到确认下一事件是工具调用（中间轮次）才显示，
    /// 避免与最终 Assistant 回复重复。
    /// </summary>
    private void AppendProcess(AgentLogEvent eventInfo)
    {
        switch (eventInfo.Kind)
        {
            case AgentLogEventKind.ModelResponse:
                _pendingModelContent = string.IsNullOrWhiteSpace(eventInfo.Result) ? null : eventInfo.Result;
                break;
            case AgentLogEventKind.ModelRequest:
                _pendingModelContent = null; // 新一轮请求开始，丢弃未用的暂存
                break;
            case AgentLogEventKind.ToolCallStarted:
                // 流式输出把本轮的模型内容实时显示在聊天列表，确认是中间轮次后
                // 撤下并移入思考面板；非流式（如上下文压缩）仍走暂存路径
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

    /// <summary>
    /// 工具状态行：有 ToolCallId 记录时就地更新（执行中 → 已完成/失败），否则追加新行。
    /// </summary>
    private void AppendToolStatus(string? toolCallId, string text)
    {
        Invoke(() =>
        {
            if (toolCallId is not null && _toolLines.TryGetValue(toolCallId, out var idx)
                && idx >= 0 && idx < _chatSource.Count)
            {
                _chatSource[idx] = text;
            }
            else
            {
                _chatSource.Add(text);
                _chatRoles.Add(ChatRole.Tool);
                if (toolCallId is not null)
                {
                    _toolLines[toolCallId] = _chatSource.Count - 1;
                }
            }
            _chat!.SelectedItem = _chatSource.Count - 1;
        });
    }

    private static string ToolName(AgentLogEvent eventInfo) =>
        string.IsNullOrWhiteSpace(eventInfo.ToolName) ? "unknown" : eventInfo.ToolName;

    /// <summary>思考面板：显示 Agent is Thinking…（清空累积的中间输出）。</summary>
    private void ShowThinking()
    {
        Invoke(() =>
        {
            _paneText.Clear();
            _pane!.Text = "· Agent is Thinking…";
            _pane.MoveEnd();
        });
    }

    /// <summary>把确认是中间轮次的模型输出累积进思考面板，并滚到底部（只显示最后几行）。</summary>
    private void FlushPendingModel()
    {
        if (_pendingModelContent is null)
        {
            return;
        }
        var content = _pendingModelContent;
        _pendingModelContent = null;
        Invoke(() =>
        {
            if (_paneText.Length > 0)
            {
                _paneText.Append('\n');
            }
            _paneText.Append(content.Replace("\r", string.Empty));
            _pane!.Text = _paneText.ToString();
            _pane.MoveEnd(); // 滚到底部，只显示最后几行
        });
    }

    /// <summary>清空思考面板并清理工具状态行索引。</summary>
    private void ClearPane()
    {
        Invoke(() =>
        {
            _toolLines.Clear();
            _paneText.Clear();
            _pane!.Text = string.Empty;
        });
    }

    /// <summary>
    /// 模型文本增量：累积到缓冲并挂一次性节流刷新（50ms），把高频 token 事件合并
    /// 为低频 UI 重绘，避免每 token 一次跨线程调度卡住终端。
    /// </summary>
    private void AppendStreamingDelta(AgentLogEvent eventInfo)
    {
        var delta = eventInfo.Result;
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }
        lock (_streamSync)
        {
            (_streamingBuffer ??= new StringBuilder()).Append(delta);
        }
        if (_streamingRefreshQueued)
        {
            return;
        }
        _streamingRefreshQueued = true;
        Invoke(() => _app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            _streamingRefreshQueued = false;
            RefreshStreamingRow();
            return false; // 只执行一次，后续增量会重新挂载
        }));
    }

    /// <summary>
    /// 把累积的流式内容重写为聊天列表末尾的 Assistant 行区间（UI 线程执行）。
    /// 行数变化时只增删差额行，其余行就地更新，尽量减少 ObservableCollection 变更。
    /// </summary>
    private void RefreshStreamingRow()
    {
        string text;
        lock (_streamSync)
        {
            if (_streamingBuffer is not { Length: > 0 })
            {
                return;
            }
            text = _streamingBuffer.ToString();
        }
        if (_streamLineStart < 0)
        {
            _streamLineStart = _chatSource.Count;
        }

        var lines = text.Replace("\r", string.Empty).Split('\n');
        var prefix = RolePrefix("Assistant");
        var indent = new string(' ', TextWidth(prefix));
        int existing = _chatSource.Count - _streamLineStart;
        int common = Math.Min(existing, lines.Length);
        for (int i = 0; i < common; i++)
        {
            _chatSource[_streamLineStart + i] = i == 0 ? prefix + lines[i] : indent + lines[i];
        }
        if (lines.Length < existing)
        {
            while (_chatSource.Count > _streamLineStart + lines.Length)
            {
                _chatSource.RemoveAt(_chatSource.Count - 1);
                _chatRoles.RemoveAt(_chatRoles.Count - 1);
            }
        }
        else
        {
            for (int i = common; i < lines.Length; i++)
            {
                _chatSource.Add(i == 0 ? prefix + lines[i] : indent + lines[i]);
                _chatRoles.Add(ChatRole.Assistant);
            }
        }
        if (_chatSource.Count > 0)
        {
            _chat!.SelectedItem = _chatSource.Count - 1;
        }
    }

    /// <summary>
    /// 中间轮（工具调用）流式内容收尾：把已实时显示的模型输出从聊天列表撤下，
    /// 追加进思考面板（与 FlushPendingModel 同一展示通道），随后显示工具状态行。
    /// </summary>
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
        if (content is null)
        {
            return;
        }
        Invoke(() =>
        {
            if (lineStart >= 0 && lineStart < _chatSource.Count)
            {
                while (_chatSource.Count > lineStart)
                {
                    _chatSource.RemoveAt(_chatSource.Count - 1);
                    _chatRoles.RemoveAt(_chatRoles.Count - 1);
                }
            }
            if (_paneText.Length > 0)
            {
                _paneText.Append('\n');
            }
            _paneText.Append(content.Replace("\r", string.Empty));
            _pane!.Text = _paneText.ToString();
            _pane.MoveEnd(); // 滚到底部，只显示最后几行
        });
    }

    /// <summary>最终回复定稿：有流式缓冲就做最后一次重写（缓冲即完整回复），
    /// 无缓冲（空回复/占位）时用 ChatCompleted 的结果兜底写入一行。</summary>
    private void FinalizeStreaming(AgentLogEvent eventInfo)
    {
        Invoke(() =>
        {
            lock (_streamSync)
            {
                if (_streamingBuffer is { Length: > 0 })
                {
                    RefreshStreamingRow();
                }
            }
            if (_streamingBuffer is null && !string.IsNullOrWhiteSpace(eventInfo.Result))
            {
                AppendChat("Assistant", eventInfo.Result);
            }
            ResetStreamingState();
        });
    }

    /// <summary>丢弃流式状态：清空缓冲与节流标志（可能还有已挂的刷新任务，届时空缓冲直接跳过）。</summary>
    private void ResetStreamingState()
    {
        lock (_streamSync)
        {
            _streamingBuffer = null;
        }
        _streamLineStart = -1;
        _streamingRefreshQueued = false;
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
