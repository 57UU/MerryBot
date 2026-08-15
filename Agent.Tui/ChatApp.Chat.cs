using System.Threading.Channels;
using Agent.Session;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- chat ----------

    /// <summary>
    /// 入队一条聊天消息并立即回显；若聊天进行中则排队，完成后自动继续。
    /// 输入框在聊天期间保持可用（常驻），可连续输入。
    /// </summary>
    private void QueueChat(string input)
    {
        AppendChat("You", input);
        _chatQueue.Writer.TryWrite(input);
        _chatPump ??= Task.Run(ChatPumpAsync);
        if (_chatRunning)
        {
            AppendChat("sys", $"已排队（队列 {Volatile.Read(ref _pendingCount) + 1}），处理完上一条后自动继续。");
        }
        Interlocked.Increment(ref _pendingCount);
        RefreshStatus();
    }

    /// <summary>串行消费聊天队列：一次只跑一条消息。</summary>
    private async Task ChatPumpAsync()
    {
        await foreach (var msg in _chatQueue.Reader.ReadAllAsync())
        {
            _chatRunning = true;
            try
            {
                await RunChatAsync(msg);
            }
            catch (Exception ex)
            {
                AppendChat("error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _chatRunning = false;
                Interlocked.Decrement(ref _pendingCount);
                RefreshStatus();
            }
        }
    }

    private async Task RunChatAsync(string input)
    {
        var (p, m) = _cfg.ResolveActive();
        if (p is null || string.IsNullOrEmpty(m))
        {
            AppendChat("sys", "未配置活动模型，请先用 /provider add 添加供应商并勾选模型。");
            return;
        }
        if (string.IsNullOrEmpty(p.ApiKey))
        {
            AppendChat("sys", $"供应商 {p.Name} 未设置 API Key，请用 /provider edit 补上。");
            return;
        }

        var session = _session ?? await (_sessionManager ?? throw new InvalidOperationException("会话未绑定"))
            .GetSessionAsync(SessionId);
        _session = session;

        using var cts = new CancellationTokenSource();
        _currentCts = cts;
        try
        {
            // 最终回复展示由 ModelTextDelta 增量 + ChatCompleted 定稿驱动，此处不再
            // 经 messageChannel 重复写入（否则会与流式行重叠）
            await session.ChatAndWaitAsync(input, _ => { }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendChat("sys", "[已取消]");
        }
        catch (Exception ex)
        {
            AppendChat("error", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _currentCts = null;
            RefreshStatus();
        }
    }

    // ---------- context commands ----------

    private async Task DoNewAsync()
    {
        var session = _session ?? await _sessionManager!.GetSessionAsync(SessionId);
        _session = session;
        await session.ResetAsync();
        AppendChat("sys", "[ctx] 已清空当前会话上下文。");
    }

    private async Task DoCompactAsync()
    {
        var session = _session ?? await _sessionManager!.GetSessionAsync(SessionId);
        _session = session;
        await session.CompactAsync(CancellationToken.None);
        AppendChat("sys", "[ctx] 已压缩上下文。");
    }

    private async Task DoRefreshAsync()
    {
        AppendChat("sys", "正在刷新 models.dev 目录…");
        await _catalog.RefreshAsync(CancellationToken.None);
        AppendChat("sys", _catalog.IsLoaded ? "models.dev 目录已刷新。" : "刷新失败，请检查网络。");
    }

    private void DoStop()
    {
        if (_currentCts is { } cts)
        {
            cts.Cancel();
            AppendChat("sys", "[stop] 已请求取消当前对话。");
        }
        else
        {
            AppendChat("sys", "[stop] 当前无进行中的对话。");
        }
    }

    private void DoStatus()
    {
        var (p, m) = _cfg.ResolveActive();
        var tokens = _session?.SessionUsage.totalUsage ?? 0;
        AppendChat("sys", $"provider: {p?.Name ?? "-"} ({p?.Id ?? "-"})\nmodel: {m ?? "-"}\ndebug: {(_debug ? "on" : "off")}\ntokens: {tokens}\ncatalog: {(_catalog.IsLoaded ? "loaded" : "not loaded")}");
    }
}
