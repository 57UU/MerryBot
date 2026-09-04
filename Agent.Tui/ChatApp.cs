using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Agent.Session;
using Agent.Tools;
using Agent.Tui.Lib;
using BrowserService;
using LlmBackend;
using LlmClient;

namespace Agent.Tui;

/// <summary>
/// TUI 编排器(自研轻量渲染,借鉴 pi 的设计)——纯文本聊天 + 底部输入行,无按钮无弹窗。
/// 布局:聊天区 / 思考面板 / 输入行 / 状态栏;选择器打开时在输入行上方覆盖。
///
/// 渲染模型:组件是无状态纯函数 —— 后台线程改状态后调 <see cref="_app.Invalidate"/>,
/// 主循环拉取 ScreenRoot 全量重渲染,差分输出。不再有跨线程 Invoke / ObservableCollection。
/// 对话执行跑在后台 pump(与输入并行,输入框常驻可排队)。
/// </summary>
public sealed partial class ChatApp : IDisposable
{
    public const string SessionId = "agent-tui-default";

    private readonly TuiConfig _cfg;
    private readonly CatalogService _catalog;
    private readonly Client _llmClient;
    private readonly ContextHistory _history;
    private readonly TuiApp _app;

    // ---- 会话与运行期组件 ----
    private AgentSessionManager? _sessionManager;
    private ClockService? _clockService;
    private Browser? _browser;
    private ConcurrentBag<TerminalToolSet>? _terminalToolSets;
    private AgentSession? _session;
    private volatile CancellationTokenSource? _currentCts;

    // ---- UI 组件 ----
    private readonly ChatView _chat = new();
    private readonly Input _input = new() { Prefix = "❯ " };
    private readonly StringBuilder _paneText = new();
    private string _paneLine = string.Empty; // 思考面板当前行(单行显示最后一段)
    private IComponent? _picker;

    // ---- 聊天消息队列:输入框常驻,聊天期间可继续输入排队 ----
    private readonly Channel<string> _chatQueue = Channel.CreateUnbounded<string>();
    private Task? _chatPump;
    private int _pendingCount;

    // ---- 流式渲染状态(Agent 线程写入加锁,渲染循环读取) ----
    private readonly object _streamSync = new();
    private StringBuilder? _streamingBuffer;
    private int _streamLineStart = -1;
    // 中间轮模型输出暂存
    private string? _pendingModelContent;

    // ---- 内联流程状态 ----
    private TaskCompletionSource<string?>? _promptTcs;
    private string? _promptDefault;
    private volatile bool _modalFlow;
    private bool _debug;

    /// <summary>聊天行角色,驱动行级颜色。</summary>
    private enum ChatRole { System, User, Assistant, Error, Debug, Tool, Cron }

    public ChatApp(TuiConfig cfg, Client llmClient, ContextHistory history, CatalogService catalog)
    {
        _cfg = cfg;
        _llmClient = llmClient;
        _history = history;
        _catalog = catalog;
        _app = new TuiApp
        {
            ScreenRoot = BuildScreen,
            OnUnhandledInput = OnUnhandledKey,
        };
        _input.OnSubmit = OnInputSubmit;
        _input.OnEscape = OnInputEscape;
        _app.Focused = _input;

        AppendChat("sys", "就绪。输入 /help 查看命令;普通文本即与助手对话。");
        var (p, _) = _cfg.ResolveActive();
        if (p is null || string.IsNullOrEmpty(p.ApiKey))
        {
            AppendChat("sys", "未配置 API Key。输入 /provider add 添加供应商并填入 Key。");
        }
    }

    /// <summary>供 Program 启动主循环。</summary>
    public TuiApp App => _app;

    /// <summary>绑定运行期组件(在 Program 创建好 sessionManager 等之后调用)。</summary>
    public void Bind(AgentSessionManager sessionManager, ClockService clockService,
        Browser browser, ConcurrentBag<TerminalToolSet> terminalToolSets)
    {
        _sessionManager = sessionManager;
        _clockService = clockService;
        _browser = browser;
        _terminalToolSets = terminalToolSets;
    }

    public void SetSession(AgentSession session) => _session = session;

    public void Dispose()
    {
        _app.Dispose();
    }

    // ================= 布局 =================

    /// <summary>完整屏幕布局:聊天区 + (选择器) + 思考面板 + 输入行 + 状态栏。</summary>
    private string[] BuildScreen()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        if (width <= 0 || height <= 0) return [];

        var pickerLines = _picker?.Render(width) ?? [];
        // 固定:思考面板 1 行 + 输入行 1 行 + 状态栏 1 行 = 3 行
        var fixedRows = 3;
        var chatHeight = Math.Max(1, height - fixedRows - pickerLines.Length);

        // 流式刷新:渲染前把累积的模型增量刷入聊天区(每帧一次,实现实时输出)
        FlushStreamingToChat();

        var lines = new List<string>(height);
        lines.AddRange(_chat.RenderViewport(width, chatHeight));
        if (pickerLines.Length > 0)
        {
            lines.AddRange(pickerLines);
        }
        // 思考面板
        lines.Add(string.IsNullOrEmpty(_paneLine) ? string.Empty : Ansi.Dim + _paneLine + Ansi.Reset);
        // 输入行
        lines.AddRange(_input.Render(width));
        // 状态栏
        lines.Add(Ansi.Dim + BuildStatusText() + Ansi.Reset);
        return lines.ToArray();
    }

    private string BuildStatusText()
    {
        var (p, m) = _cfg.ResolveActive();
        var usage = _session?.SessionUsage;
        var tokens = usage?.totalUsage ?? 0;
        var cache = usage is { promptUsage: > 0 }
            ? $" | cache: {usage.cachedUsage * 100.0 / usage.promptUsage:0}%"
            : string.Empty;
        var queue = Volatile.Read(ref _pendingCount) > 0 ? $" | queue: {_pendingCount}" : string.Empty;
        return $"model: {m ?? "-"} | provider: {p?.Name ?? "-"} | debug: {(_debug ? "on" : "off")} | tokens: {tokens}{cache}{queue}";
    }

    /// <summary>通知主循环刷新一帧(任意线程可调)。</summary>
    public void Invalidate() => _app.Invalidate();

    // ================= 输入入口 =================

    private void OnInputSubmit(string value)
    {
        _input.Value = string.Empty;
        if (_promptTcs is { } tcs)
        {
            _promptTcs = null;
            var result = string.IsNullOrEmpty(value) ? _promptDefault : value.Trim();
            tcs.TrySetResult(result);
            _input.PrefixProvider = null;
            return;
        }
        if (_modalFlow)
        {
            return; // 选择器/向导进行中,普通输入不生效
        }
        DispatchInput(value ?? string.Empty);
    }

    private void OnInputEscape()
    {
        if (_promptTcs is { } tcs)
        {
            _promptTcs = null;
            tcs.TrySetResult(null);
            _input.PrefixProvider = null;
            _input.Value = string.Empty;
        }
    }

    /// <summary>未被组件消费的全局键:Ctrl+C 退出。</summary>
    private void OnUnhandledKey(KeyEvent ev)
    {
        if (ev.Key == Key.Char && ev.Ctrl && ev.Char is 'c' or '\x03')
        {
            _app.RequestStop();
        }
    }

    // ================= 提示模式 =================

    /// <summary>提示模式:在输入行显示问题,等待一次 Enter(空输入用默认值,Esc 返回 null)。</summary>
    private Task<string?> PromptAsync(string question, string? defaultValue)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _promptTcs = tcs;
        _promptDefault = defaultValue;
        _input.PrefixProvider = () => question;
        _input.Value = defaultValue ?? string.Empty;
        _app.Focused = _input;
        Invalidate();
        return tcs.Task;
    }

    /// <summary>挂载选择器并等待结果;结束后移除并恢复输入框。null = 取消。</summary>
    private async Task<List<SelectList<T>.Item>?> PickAsync<T>(SelectList<T> picker)
    {
        var tcs = new TaskCompletionSource<List<SelectList<T>.Item>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _modalFlow = true;
        picker.OnDone = result =>
        {
            ModalCleanup();
            tcs.TrySetResult(result);
        };
        _picker = picker;
        _app.Focused = picker;
        Invalidate();
        var result = await tcs.Task;
        return result;
    }

    private void ModalCleanup()
    {
        _picker = null;
        _modalFlow = false;
        _app.Focused = _input;
        Invalidate();
    }

    /// <summary>Cron 等无显式 channel 消息的默认展示通道:追加到聊天日志。</summary>
    public void OnCronMessage(string response) => AppendChat("Cron", response);
}