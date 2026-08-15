using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using Agent.Session;
using Agent.Tools;
using Agent.Tui.Views;
using BrowserService;
using LlmBackend;
using LlmClient;
using ModelsDev.Sdk.Models;
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
/// </summary>
public sealed class ChatApp
{
    public const string SessionId = "agent-tui-default";

    private const string PromptIdleText = "❯ ";

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
    // 工具状态行：ToolCallId → 聊天行索引（执行中 → 已完成/失败，就地更新）
    private readonly Dictionary<string, int> _toolLines = [];

    // 内联流程状态：提示模式（输入行显示问题，等待一次 Enter）
    private TaskCompletionSource<string?>? _promptTcs;
    private string? _promptDefault;
    // 内联流程进行中（选择器/向导），此时输入行不响应普通命令
    private volatile bool _modalFlow;

    /// <summary>聊天行角色，驱动行级颜色。</summary>
    private enum ChatRole { System, User, Assistant, Error, Debug, Tool, Cron }

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

    // ---------- UI helpers ----------

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            action();
        }
        else
        {
            _app.Invoke(action);
        }
    }

    private void AppendChat(string role, string text)
    {
        Invoke(() =>
        {
            var chatRole = RoleOf(role);
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            // 只有首行带 emoji 前缀，续行按前缀显示宽度对齐缩进
            var prefix = RolePrefix(role);
            var indent = new string(' ', TextWidth(prefix));
            for (int i = 0; i < lines.Length; i++)
            {
                _chatSource.Add(i == 0 ? prefix + lines[i] : indent + lines[i]);
                _chatRoles.Add(chatRole);
            }
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    /// <summary>追加一行无前缀的原始文本（带角色颜色），用于工具结果摘要等。</summary>
    private void AppendLine(ChatRole role, string text)
    {
        Invoke(() =>
        {
            _chatSource.Add(text);
            _chatRoles.Add(role);
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
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
            _chatRoles.Add(ChatRole.Debug);
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    private void RefreshStatus()
    {
        Invoke(() =>
        {
            var (p, m) = _cfg.ResolveActive();
            var usage = _session?.SessionUsage;
            var tokens = usage?.totalUsage ?? 0;
            var cache = usage is { promptUsage: > 0 }
                ? $" | cache: {usage.cachedUsage * 100.0 / usage.promptUsage:0}%"
                : string.Empty;
            var queue = Volatile.Read(ref _pendingCount) > 0 ? $" | queue: {_pendingCount}" : string.Empty;
            _status!.Text = $"model: {m ?? "-"} | provider: {p?.Name ?? "-"} | debug: {(_debug ? "on" : "off")} | tokens: {tokens}{cache}{queue}";
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

    /// <summary>点击窗口空白处/聊天区时，把焦点还给输入框（滚轮滚动不受影响）。</summary>
    private void OnBlankClick(Mouse e)
    {
        if (e.IsSingleClicked)
        {
            e.Handled = true;
            _input!.SetFocus();
        }
    }

    /// <summary>把展示用的角色名映射为行角色，用于着色。</summary>
    private static ChatRole RoleOf(string role) => role switch
    {
        "You" => ChatRole.User,
        "Assistant" => ChatRole.Assistant,
        "error" => ChatRole.Error,
        "Cron" => ChatRole.Cron,
        "tool" => ChatRole.Tool,
        _ => ChatRole.System,
    };

    /// <summary>角色对应的 emoji 前缀。</summary>
    private static string RolePrefix(string role) => role switch
    {
        "You" => "⭐ ",
        "Assistant" => "● ",
        "tool" => "● ",
        "error" => "✗ ",
        "Cron" => "⏰ ",
        _ => "· ", // sys 等
    };

    private static Color RoleColor(ChatRole role) => role switch
    {
        ChatRole.User => Color.Yellow, // 金黄色
        ChatRole.Assistant => Color.White,
        ChatRole.Error => Color.Red,
        ChatRole.Cron => Color.Yellow,
        ChatRole.Tool => Color.Green,
        _ => Color.DarkGray,
    };

    /// <summary>按终端显示宽度计算（emoji/全角=2 列，ASCII=1 列）。</summary>
    private static int TextWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.GetColumns();
        }
        return width;
    }
    /// <summary>单一前景色、终端默认背景（Color.None）的 Scheme，用于状态栏/提示符等弱化或强调元素。</summary>
    private static Scheme SingleColorScheme(Color foreground) => new(new Attribute(foreground, Color.None));

    // ---------- command dispatch ----------

    private const string HelpText = """
        /help            显示本帮助
        /model [query]   选择活动模型（带参数直接匹配，如 /model deepseek）
        /provider        供应商管理：list / add / edit <n> / models <n> / remove <n>
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
            QueueChat(input);
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
                RunFlow(() => OpenModelPickerAsync(arg));
                break;
            case "provider":
                RunFlow(() => RunProviderCommandAsync(arg));
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

    /// <summary>
    /// 内联流程（选择器/向导）的后台运行器：整个流程期间置 <see cref="_modalFlow"/>，
    /// 吞掉输入行的普通命令（提示模式除外），结束统一恢复输入。
    /// </summary>
    private void RunFlow(Func<Task> action)
    {
        _modalFlow = true;
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
                _modalFlow = false;
                SetInputEnabled(true);
            }
        });
    }

    /// <summary>输入行回车：优先结束提示模式，其次执行命令；流程进行中吞掉普通输入。</summary>
    private void OnInputAccepting(object? sender, CommandEventArgs e)
    {
        e.Handled = true;
        var text = _input!.Text;
        _input.Text = string.Empty;
        if (_promptTcs is { } tcs)
        {
            _promptTcs = null;
            var value = string.IsNullOrEmpty(text) ? _promptDefault : text.Trim();
            tcs.TrySetResult(value);
            _promptLabel!.Text = PromptIdleText;
            return;
        }
        if (_modalFlow)
        {
            return; // 选择器/向导进行中，普通输入不生效
        }
        DispatchInput(text ?? string.Empty);
    }

    /// <summary>输入行 Esc：提示模式中取消当前提问。</summary>
    private void OnInputKeyDown(object? sender, Key key)
    {
        if (_promptTcs is { } tcs && key == Key.Esc)
        {
            key.Handled = true;
            _promptTcs = null;
            tcs.TrySetResult(null);
            _promptLabel!.Text = PromptIdleText;
            _input!.Text = string.Empty;
        }
    }

    /// <summary>提示模式：在输入行显示问题，等待一次 Enter（空输入用默认值，Esc 返回 null）。</summary>
    private Task<string?> PromptAsync(string question, string? defaultValue)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() =>
        {
            _promptTcs = tcs;
            _promptDefault = defaultValue;
            _promptLabel!.Text = question;
            _input!.Text = defaultValue ?? string.Empty;
            SetInputEnabled(true);
        });
        return tcs.Task;
    }

    /// <summary>挂载并等待一个内联选择器；结束后移除并恢复输入框。</summary>
    private async Task<List<PickList.Item>?> PickAsync(PickList picker)
    {
        Invoke(() =>
        {
            SetInputEnabled(false);
            _window!.Add(picker);
            picker.FocusFilter();
        });
        var result = await picker.WaitAsync();
        Invoke(() =>
        {
            _window!.Remove(picker);
            SetInputEnabled(true);
            _input!.SetFocus();
        });
        return result;
    }

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

    // ---------- model selection ----------

    private sealed record ModelRow(string ProviderId, string ModelId, string Display);

    private async Task OpenModelPickerAsync(string query)
    {
        if (!_catalog.IsLoaded)
        {
            await _catalog.EnsureLoadedAsync(CancellationToken.None);
        }
        var (activeProvider, activeModel) = _cfg.ResolveActive();
        var rows = BuildModelRows();
        if (rows.Count == 0)
        {
            AppendChat("sys", "还没有可用模型。先输入 /provider add 添加供应商并勾选模型。");
            return;
        }

        if (!string.IsNullOrEmpty(query))
        {
            var hits = rows
                .Where(r => r.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 1)
            {
                ApplySelection(hits[0].ProviderId, hits[0].ModelId);
                return;
            }
            AppendChat("sys", hits.Count == 0
                ? $"没有匹配 “{query}” 的模型，已打开选择列表："
                : $"“{query}” 匹配到 {hits.Count} 个模型，已打开选择列表：");
        }

        var activeIdx = rows.FindIndex(r => r.ProviderId == activeProvider?.Id && r.ModelId == activeModel);
        var items = rows.Select(r => new PickList.Item(r.Display, r)).ToList();
        var picker = new PickList("选择模型", items, preSelected: activeIdx);
        var result = await PickAsync(picker);
        if (result is { Count: 1 })
        {
            var row = (ModelRow)result[0].Payload!;
            ApplySelection(row.ProviderId, row.ModelId);
        }
    }

    private List<ModelRow> BuildModelRows()
    {
        var (activeProvider, activeModel) = _cfg.ResolveActive();
        var rows = new List<ModelRow>();
        foreach (var p in _cfg.Providers)
        {
            foreach (var m in p.Models)
            {
                var display = $"{p.Name} / {m}";
                if (_catalog.IsLoaded && _catalog.GetProvider(p.Id) is { } info
                    && info.Models.GetValueOrDefault(m) is { } modelInfo)
                {
                    display += $" — {modelInfo.Name}";
                }
                if (p.Id == activeProvider?.Id && m == activeModel)
                {
                    display = "[active] " + display;
                }
                rows.Add(new ModelRow(p.Id, m, display));
            }
        }
        return rows;
    }

    private void ApplySelection(string providerId, string modelId)
    {
        _cfg.Active.Provider = providerId;
        _cfg.Active.Model = modelId;
        var p = _cfg.FindProvider(providerId);
        _backend.Update(p?.ApiBase ?? string.Empty, p?.ApiKey ?? string.Empty, modelId);
        TuiConfigStore.Save(_cfg);
        RefreshStatus();
        AppendChat("sys", $"已切换到 {p?.Name ?? providerId} / {modelId}。");
    }

    // ---------- provider management (inline flows) ----------

    private async Task RunProviderCommandAsync(string arg)
    {
        var parts = string.IsNullOrEmpty(arg)
            ? Array.Empty<string>()
            : arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var numArg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (sub)
        {
            case "" or "list":
                ListProviders();
                return;
            case "add":
                await RunProviderAddAsync();
                return;
            case "edit":
                await RunProviderEditAsync(numArg);
                return;
            case "models":
                await RunProviderModelsAsync(numArg);
                return;
            case "remove":
                await RunProviderRemoveAsync(numArg);
                return;
            default:
                AppendChat("sys", "用法: /provider [list | add | edit <n> | models <n> | remove <n>]");
                return;
        }
    }

    private void ListProviders()
    {
        var providers = _cfg.Providers;
        if (providers.Count == 0)
        {
            AppendChat("sys", "还没有供应商。输入 /provider add 添加。");
            return;
        }
        var (activeProvider, _) = _cfg.ResolveActive();
        for (int i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var star = p.Id == activeProvider?.Id ? " ★" : string.Empty;
            AppendChat("sys", $"{i + 1}. {p.Name} ({p.Id}) [{p.Models.Count} models]{star}");
        }
        AppendChat("sys", "子命令: add · edit <n> · models <n> · remove <n>");
    }

    private async Task RunProviderAddAsync()
    {
        await _catalog.EnsureLoadedAsync(CancellationToken.None);
        var catalogProviders = _catalog.IsLoaded
            ? _catalog.GetAllProviders().OrderBy(p => p.Name).ToList()
            : [];
        if (catalogProviders.Count == 0)
        {
            AppendChat("sys", "models.dev 目录不可用，无法选择供应商。");
            return;
        }

        var items = catalogProviders.Select(p => new PickList.Item($"{p.Name} ({p.Id})", p)).ToList();
        var picker = new PickList("选择供应商", items);
        var picked = await PickAsync(picker);
        if (picked is not { Count: 1 })
        {
            return;
        }
        var provider = (Provider)picked[0].Payload!;

        var apiBase = await PromptAsync("API Base（回车用默认）: ", provider.Api ?? string.Empty);
        if (apiBase is null)
        {
            return;
        }
        var apiKey = await PromptAsync("API Key: ", string.Empty);
        if (apiKey is null)
        {
            return;
        }

        var models = _catalog.IsLoaded ? _catalog.GetModels(provider.Id) : Array.Empty<ModelInfo>();
        if (models.Count == 0)
        {
            AppendChat("sys", $"目录中 {provider.Name} 没有可用模型。");
            return;
        }
        var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
        var modelPicker = new PickList("勾选模型", modelItems, multi: true);
        var chosen = await PickAsync(modelPicker);
        if (chosen is not { Count: > 0 })
        {
            return;
        }

        var config = new ProviderConfig
        {
            Id = provider.Id,
            Name = provider.Name,
            ApiBase = apiBase,
            ApiKey = apiKey,
            Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList(),
        };
        _cfg.Providers.Add(config);
        if (string.IsNullOrEmpty(_cfg.Active.Provider))
        {
            _cfg.Active.Provider = config.Id;
            _cfg.Active.Model = config.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已添加供应商 {config.Name}（{config.Models.Count} 个模型）。");
    }

    private async Task RunProviderEditAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var existing = _cfg.Providers[idx];

        await _catalog.EnsureLoadedAsync(CancellationToken.None);

        var apiBase = await PromptAsync("API Base（回车不变）: ", existing.ApiBase);
        if (apiBase is null)
        {
            return;
        }
        var apiKey = await PromptAsync("API Key（回车不变）: ", existing.ApiKey);
        if (apiKey is null)
        {
            return;
        }

        var models = _catalog.IsLoaded ? _catalog.GetModels(existing.Id) : Array.Empty<ModelInfo>();
        if (models.Count > 0)
        {
            var preChecked = new List<int>();
            for (int i = 0; i < models.Count; i++)
            {
                if (existing.Models.Contains(models[i].Id))
                {
                    preChecked.Add(i);
                }
            }
            var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
            var modelPicker = new PickList("勾选模型", modelItems, multi: true, preChecked: preChecked);
            var chosen = await PickAsync(modelPicker);
            if (chosen is null)
            {
                return;
            }
            if (chosen.Count > 0)
            {
                existing.Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList();
            }
        }
        else
        {
            AppendChat("sys", "目录不可用，模型保持不变。");
        }

        existing.ApiBase = apiBase;
        existing.ApiKey = apiKey;
        if (_cfg.Active.Provider == existing.Id)
        {
            _cfg.Active.Model = existing.Models.Contains(_cfg.Active.Model ?? string.Empty)
                ? _cfg.Active.Model
                : existing.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已更新供应商 {existing.Name}。");
    }

    private async Task RunProviderModelsAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var existing = _cfg.Providers[idx];

        await _catalog.EnsureLoadedAsync(CancellationToken.None);
        var models = _catalog.IsLoaded ? _catalog.GetModels(existing.Id) : Array.Empty<ModelInfo>();
        if (models.Count == 0)
        {
            AppendChat("sys", "目录不可用或该供应商没有模型。");
            return;
        }

        var preChecked = new List<int>();
        for (int i = 0; i < models.Count; i++)
        {
            if (existing.Models.Contains(models[i].Id))
            {
                preChecked.Add(i);
            }
        }
        var modelItems = models.Select(m => new PickList.Item($"{m.Id} — {m.Name}", m)).ToList();
        var picker = new PickList("勾选模型", modelItems, multi: true, preChecked: preChecked);
        var chosen = await PickAsync(picker);
        if (chosen is null)
        {
            return;
        }
        if (chosen.Count == 0)
        {
            AppendChat("sys", "至少勾选一个模型。");
            return;
        }
        existing.Models = chosen.Select(i => ((ModelInfo)i.Payload!).Id).ToList();
        if (_cfg.Active.Provider == existing.Id)
        {
            _cfg.Active.Model = existing.Models.Contains(_cfg.Active.Model ?? string.Empty)
                ? _cfg.Active.Model
                : existing.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已更新 {existing.Name} 的模型列表。");
    }

    private async Task RunProviderRemoveAsync(string numArg)
    {
        var idx = ParseIndex(numArg);
        if (idx < 0)
        {
            ListProviders();
            return;
        }
        if (idx >= _cfg.Providers.Count)
        {
            AppendChat("sys", $"没有第 {idx + 1} 个供应商。");
            return;
        }
        var target = _cfg.Providers[idx];

        var confirm = await PromptAsync($"确认删除 {target.Name} ({target.Id})? (y/N): ", string.Empty);
        if (confirm is null)
        {
            return;
        }
        if (!confirm.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            AppendChat("sys", "已取消删除。");
            return;
        }
        _cfg.Providers.RemoveAt(idx);
        if (_cfg.Active.Provider == target.Id)
        {
            var first = _cfg.Providers.FirstOrDefault();
            _cfg.Active.Provider = first?.Id;
            _cfg.Active.Model = first?.Models.FirstOrDefault();
        }
        SaveAndSync();
        AppendChat("sys", $"已删除供应商 {target.Name}。");
    }

    private void SaveAndSync()
    {
        var (p, m) = _cfg.ResolveActive();
        _backend.Update(p?.ApiBase ?? string.Empty, p?.ApiKey ?? string.Empty, m);
        TuiConfigStore.Save(_cfg);
        RefreshStatus();
    }

    private static int ParseIndex(string numArg)
        => int.TryParse(numArg, out var n) && n >= 1 ? n - 1 : -1;

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
            case AgentLogEventKind.ChatStarted:
                ShowThinking(); // 响应期间显示 Agent is Thinking
                break;
            case AgentLogEventKind.ChatCompleted:
            case AgentLogEventKind.ChatFailed:
                // 最终回复由 messageChannel 以 Assistant 行展示，丢弃暂存并清空思考面板
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
