using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using Agent.Session;
using Agent.Tools;
using BrowserService;
using LlmBackend;
using LlmClient;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

#pragma warning disable CS0618 // TextView 在 2.4.17 中标记过时（建议换 tui-cs/Editor），但仍是当前唯一可用的滚动文本视图

namespace Agent.Tui;

/// <summary>
/// TUI 编排器：构建无边框主窗口，分发斜杠命令，桥接 Agent 会话与 Terminal.Gui 主循环。
/// 风格仿 Claude Code：纯文本聊天 + 底部输入行，所有配置交互走内联选择器/输入行提示，无按钮无弹窗。
/// 所有 UI 更新经 <see cref="Invoke"/> 回主线程；对话执行跑在后台 <see cref="Task.Run"/>。
/// 按职责拆分为 partial 文件：Render（聊天渲染）/ Input（输入分发）/ Chat（聊天执行与通用命令）/
/// Provider（供应商与模型管理）/ Events（Agent 事件与流式渲染）。
/// </summary>
public sealed partial class ChatApp
{
    public const string SessionId = "agent-tui-default";

    private const string PromptIdleText = "❯ ";

    private readonly IApplication _app;
    private readonly TuiConfig _cfg;
    private readonly CatalogService _catalog;
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
    private Label? _promptLabel;
    private ListView? _chat;
    private readonly ObservableCollection<string> _chatSource = [];
    // 与 _chatSource 一一对应的行角色，驱动行级着色
    private readonly List<ChatRole> _chatRoles = [];
    private TextField? _input;

    private bool _debug;
    private CancellationTokenSource? _currentCts;

    // 聊天消息队列：输入框常驻，聊天期间可继续输入排队
    private readonly Channel<string> _chatQueue = Channel.CreateUnbounded<string>();
    private Task? _chatPump;
    private volatile bool _chatRunning;
    private int _pendingCount;

    // 过程事件渲染：暂存模型中间输出，等确认是中间轮次后写入思考面板
    private string? _pendingModelContent;
    private readonly StringBuilder _paneText = new();
    private TextView? _pane;
    // 流式渲染：模型增量累积到缓冲（Agent 线程写入，加锁防撕裂），
    // 由节流任务（UI 线程）重写聊天列表末尾的 Assistant 行区间
    private readonly object _streamSync = new();
    private StringBuilder? _streamingBuffer;
    private int _streamLineStart = -1;
    private bool _streamingRefreshQueued;
    // 工具状态行：ToolCallId → 聊天行索引（执行中 → 已完成/失败，就地更新）
    private readonly Dictionary<string, int> _toolLines = [];

    // 内联流程状态：提示模式（输入行显示问题，等待一次 Enter）
    private TaskCompletionSource<string?>? _promptTcs;
    private string? _promptDefault;
    // 内联流程进行中（选择器/向导），此时输入行不响应普通命令
    private volatile bool _modalFlow;

    /// <summary>聊天行角色，驱动行级颜色。</summary>
    private enum ChatRole { System, User, Assistant, Error, Debug, Tool, Cron }

    public ChatApp(IApplication app, TuiConfig cfg, Client llmClient,
        ContextHistory history, CatalogService catalog)
    {
        _app = app;
        _cfg = cfg;
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
        _window = new Window
        {
            BorderStyle = LineStyle.None,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        _status = new Label { X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill() };
        _status.SetScheme(SingleColorScheme(Color.DarkGray));

        _chat = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(6),
        };
        _chat.Source = new ListWrapper<string>(_chatSource);
        _chat.RowRender += (_, e) =>
        {
            if (e.Row < 0 || e.Row >= _chatRoles.Count)
            {
                return;
            }
            var baseAttr = _chat!.GetAttributeForRole(VisualRole.Normal);
            e.RowAttribute = baseAttr with { Foreground = RoleColor(_chatRoles[e.Row]) };
        };

        // 输入框：带 padding 的圆角边框盒（无底色）
        var inputBar = new View
        {
            X = 0,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(),
            Height = 3,
            BorderStyle = LineStyle.Rounded,
            CanFocus = true, // 子输入框要能获得焦点，容器必须 CanFocus
        };
        inputBar.Padding.Thickness = new Thickness(1, 0, 1, 0); // 左右各 1 列 padding

        _promptLabel = new Label { Text = PromptIdleText, X = 0, Y = 0 };
        _promptLabel.SetScheme(SingleColorScheme(Color.Green));
        _input = new TextField { X = 2, Y = 0, Width = Dim.Fill() };
        _input.Accepting += OnInputAccepting;
        _input.KeyDown += OnInputKeyDown;
        inputBar.Add(_promptLabel, _input);

        // 思考面板：固定 2 行，展示 Agent is Thinking 与中间模型输出（内部滚动，只看最后几行）
        _pane = new TextView
        {
            X = 0,
            Y = Pos.AnchorEnd(6),
            Width = Dim.Fill(),
            Height = 2,
            ReadOnly = true,
            Multiline = true,
            CanFocus = false,
        };
        _pane.SetScheme(SingleColorScheme(Color.Gray));

        _window.Add(_status, _chat, _pane, inputBar);

        // 点击窗口空白处/聊天区 → 焦点回到输入框
        _window.MouseEvent += (_, e) => OnBlankClick(e);
        _chat.MouseEvent += (_, e) => OnBlankClick(e);

        // 启动后自动聚焦输入框，无需点击。
        // 注意：Initialized 在 Begin/EndInit 期间触发，此时同步 SetFocus 可能被焦点后置检查拒绝（偶发崩溃），
        // 延迟到主循环第一轮再聚焦。
        _window.Initialized += (_, _) =>
        {
            _input!.Enabled = true;
            _app.AddTimeout(TimeSpan.Zero, () =>
            {
                _input.SetFocus();
                return false; // 只执行一次
            });
        };

        AppendChat("sys", "就绪。输入 /help 查看命令；普通文本即与助手对话。");
        var (p, _) = _cfg.ResolveActive();
        if (p is null || string.IsNullOrEmpty(p.ApiKey))
        {
            AppendChat("sys", "未配置 API Key。输入 /provider add 添加供应商并填入 Key。");
        }
        RefreshStatus();
        return _window;
    }

    /// <summary>Cron 等无显式 channel 消息的默认展示通道：追加到聊天日志。</summary>
    public void OnCronMessage(string response) => AppendChat("Cron", response);
}
