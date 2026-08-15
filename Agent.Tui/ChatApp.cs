using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Agent.Session;
using Agent.Tools;
using BrowserService;
using LlmBackend;
using LlmClient;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Agent.Tui;

/// <summary>
/// TUI 编排器：构建主窗口，分发斜杠命令，桥接 Agent 会话与 Terminal.Gui 主循环。
/// 所有 UI 更新经 <see cref="Invoke"/> 回主线程；对话执行跑在后台 <see cref="Task.Run"/>。
/// </summary>
public sealed class ChatApp
{
    public const string SessionId = "agent-tui-default";

    private readonly IApplication _app;
    private readonly TuiConfig _cfg;
    private readonly CatalogService _catalog;
    private readonly DynamicBackend _backend;
    private readonly Client _llmClient;
    private readonly ContextHistory _history;
    private readonly int _mainThreadId;

    private AgentSessionManager? _sessionManager;
    private ClockService? _clockService;
    private Browser? _browser;
    private ConcurrentBag<TerminalToolSet>? _terminalToolSets;
    private AgentSession? _session;

    private Window? _window;
    private Label? _status;
    private ListView? _chat;
    private readonly ObservableCollection<string> _chatSource = [];
    private TextField? _input;

    private bool _debug;
    private CancellationTokenSource? _currentCts;

    public ChatApp(IApplication app, TuiConfig cfg, DynamicBackend backend, Client llmClient,
        ContextHistory history, CatalogService catalog)
    {
        _app = app;
        _cfg = cfg;
        _backend = backend;
        _llmClient = llmClient;
        _history = history;
        _catalog = catalog;
        _mainThreadId = app.MainThreadId ?? -1;
    }

    /// <summary>绑定运行期组件（在 Program 创建好 sessionManager 等之后调用）。</summary>
    public void Bind(AgentSessionManager sessionManager, ClockService clockService,
        Browser browser, ConcurrentBag<TerminalToolSet> terminalToolSets)
    {
        _sessionManager = sessionManager;
        _clockService = clockService;
        _browser = browser;
        _terminalToolSets = terminalToolSets;
    }

    public void SetSession(AgentSession session) => _session = session;

    /// <summary>构建并返回主窗口（仅调用一次，交由 Program 以 app.Run 运行）。</summary>
    public Window BuildMainWindow()
    {
        _window = new Window { Title = "Agent.Tui" };

        _status = new Label { X = 0, Y = 0, Width = Dim.Fill() };
        _chat = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _chat.Source = new ListWrapper<string>(_chatSource);
        _input = new TextField { X = 0, Y = Pos.Bottom(_chat), Width = Dim.Fill() };
        _input.Accepting += (_, e) =>
        {
            e.Handled = true;
            var text = _input.Text;
            _input.Text = string.Empty;
            DispatchInput(text ?? string.Empty);
        };

        _window.Add(_status, _chat, _input);

        AppendChat("sys", "就绪。输入 /help 查看命令；普通文本即与助手对话。");
        var (p, _) = _cfg.ResolveActive();
        if (p is null || string.IsNullOrEmpty(p.ApiKey))
        {
            AppendChat("sys", "未配置 API Key。输入 /provider 添加供应商并填入 Key。");
        }
        RefreshStatus();
        return _window;
    }

    // ---------- UI helpers ----------

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            action();
        }
        else
        {
            Application.Invoke(action);
        }
    }

    private void AppendChat(string role, string text)
    {
        Invoke(() =>
        {
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            foreach (var line in lines)
            {
                _chatSource.Add($"{role}> {line}");
            }
            if (_chatSource.Count > 0)
            {
                _chat.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    private void AppendDebug(string line)
    {
        if (!_debug)
        {
            return;
        }
        Invoke(() =>
        {
            _chatSource.Add(line);
            if (_chatSource.Count > 0)
            {
                _chat.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    private void RefreshStatus()
    {
        Invoke(() =>
        {
            var (p, m) = _cfg.ResolveActive();
            var tokens = _session?.SessionUsage.totalUsage ?? 0;
            _status!.Text = $"model: {m ?? "-"} | provider: {p?.Name ?? "-"} | debug: {(_debug ? "on" : "off")} | tokens: {tokens}";
        });
    }

    private void SetInputEnabled(bool enabled)
    {
        Invoke(() =>
        {
            _input!.Enabled = enabled;
            if (enabled)
            {
                _input.SetFocus();
            }
        });
    }

    // ---------- command dispatch ----------

    private const string HelpText = """
        /help            显示本帮助
        /model [query]   选择活动模型
        /provider        管理（增/改/删）供应商，填 API Key、勾选模型
        /new             清空当前会话上下文
        /compact         手动压缩上下文
        /stop            取消当前对话
        /debug           切换调试日志
        /refresh         重新拉取 models.dev 目录
        /status          显示当前配置摘要
        /exit            退出
        """;

    private void DispatchInput(string raw)
    {
        var input = raw.Trim();
        if (input.Length == 0)
        {
            return;
        }
        if (input[0] != '/')
        {
            StartAsync(() => RunChatAsync(input));
            return;
        }

        var rest = input[1..];
        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = sp.Length > 0 ? sp[0].ToLowerInvariant() : string.Empty;
        var arg = sp.Length > 1 ? sp[1].Trim() : string.Empty;

        switch (cmd)
        {
            case "help":
                AppendChat("sys", HelpText);
                break;
            case "model":
                OpenModelPicker();
                break;
            case "provider":
                OpenProviderManager();
                break;
            case "new":
                StartAsync(DoNewAsync);
                break;
            case "compact":
                StartAsync(DoCompactAsync);
                break;
            case "stop":
                DoStop();
                break;
            case "debug":
                _debug = !_debug;
                AppendChat("sys", $"[debug] {(_debug ? "已开启" : "已关闭")}");
                break;
            case "refresh":
                StartAsync(DoRefreshAsync);
                break;
            case "status":
                DoStatus();
                break;
            case "exit":
                _app.RequestStop();
                break;
            default:
                AppendChat("sys", $"未知命令: {cmd}（/help 查看命令列表）");
                break;
        }
    }

    /// <summary>把异步任务丢到后台线程，执行期间禁用输入框；异常统一记录。</summary>
    private void StartAsync(Func<Task> action)
    {
        SetInputEnabled(false);
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetInputEnabled(true);
            }
        });
    }

    // ---------- chat ----------

    private async Task RunChatAsync(string input)
    {
        var (p, m) = _cfg.ResolveActive();
        if (p is null || string.IsNullOrEmpty(m))
        {
            AppendChat("sys", "未配置活动模型，请先用 /provider 添加供应商、/model 选择模型。");
            return;
        }
        if (string.IsNullOrEmpty(p.ApiKey))
        {
            AppendChat("sys", $"供应商 {p.Name} 未设置 API Key，请用 /provider edit {p.Id}。");
            return;
        }
        AppendChat("You", input);

        var session = _session ?? await (_sessionManager ?? throw new InvalidOperationException("会话未绑定"))
            .GetSessionAsync(SessionId);
        _session = session;

        using var cts = new CancellationTokenSource();
        _currentCts = cts;
        try
        {
            await session.ChatAndWaitAsync(input, response => AppendChat("Assistant", response), cts.Token);
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

    // ---------- config dialogs ----------

    private void OpenModelPicker()
    {
        var dlg = new Views.ModelPickerDialog(_app, _cfg, _catalog);
        _app.Run(dlg);
        if (dlg.Selected is { } sel)
        {
            _cfg.Active.Provider = sel.ProviderId;
            _cfg.Active.Model = sel.ModelId;
            var p = _cfg.FindProvider(sel.ProviderId);
            _backend.Update(p?.ApiBase ?? string.Empty, p?.ApiKey ?? string.Empty, sel.ModelId);
            TuiConfigStore.Save(_cfg);
            RefreshStatus();
            AppendChat("sys", $"已切换到 {p?.Name}/{sel.ModelId}。");
        }
        _input!.SetFocus();
    }

    private void OpenProviderManager()
    {
        var win = new Views.ProviderManagerWindow(_app, _cfg, _catalog);
        _app.Run(win);
        // 关闭后同步活动配置到后端
        var (p, m) = _cfg.ResolveActive();
        _backend.Update(p?.ApiBase ?? string.Empty, p?.ApiKey ?? string.Empty, m);
        TuiConfigStore.Save(_cfg);
        RefreshStatus();
        _input!.SetFocus();
    }

    /// <summary>供 Program 的 AgentOptions.OnLog 回调使用（已在主线程或后台均安全）。</summary>
    public void OnAgentLog(AgentLogEvent eventInfo) => AppendDebug(FormatLogEvent(eventInfo));

    /// <summary>Cron 等无显式 channel 消息的默认展示通道：追加到聊天日志。</summary>
    public void OnCronMessage(string response) => AppendChat("Cron", response);

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
