using System.Collections.Concurrent;
using Agent;
using Agent.Session;
using Agent.Tools;
using Agent.Tui;
using BrowserService;
using LlmBackend;
using LlmClient;
using Terminal.Gui.App;

// 记录用户启动 TUI 时的原始工作目录，传给 TerminalToolSet 作为 bash 进程的初始 CWD；
// 必须在 SetCurrentDirectory 之前捕获，否则会被 BaseDirectory 覆盖。
var originalWorkingDirectory = Environment.CurrentDirectory;

// Browser's bundled scripts are copied beside the executable. Setting the working
// directory here makes `dotnet run --project Agent.Tui` work consistently.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var skillsPath = Path.Combine(AppContext.BaseDirectory, "skills");
Directory.CreateDirectory(skillsPath);

var cfg = TuiConfigStore.Load();
var (activeProvider, activeModel) = cfg.ResolveActive();
// 直接构造真实后端：运行时切换供应商/模型经 Client.UpdateBackend 生效，
// 无需重建 Client/Agent（重试与上下文保持）
var backend = new ChatCompletionBackend(
    activeProvider?.ApiBase ?? string.Empty,
    activeProvider?.ApiKey ?? string.Empty,
    activeModel);
var llmClient = new Client(backend, new ClientConfig(3, TimeSpan.FromSeconds(1)));
var history = new PlaceholderContextHistory();
var catalog = new CatalogService();

using IApplication app = Application.Create();
app.Init();
var chatApp = new ChatApp(app, cfg, llmClient, history, catalog);

var terminalToolSets = new ConcurrentBag<TerminalToolSet>();
Browser? browser = null;
ClockService? clockService = null;
AgentSessionManager? sessionManager = null;
var shutdown = new CancellationTokenSource();

try
{
    browser = new Browser(new BrowserOptions
    {
        Headless = true,
        BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN"),
    });
    sessionManager = new AgentSessionManager(CreateAgentAsync);
    clockService = new ClockService(
        new InMemoryClockStore(),
        new DelegatingClockExecutor { Inner = new AgentSessionClockExecutor(sessionManager) });
    await clockService.StartAsync(shutdown.Token);

    chatApp.Bind(sessionManager, clockService, browser, terminalToolSets);
    var session = await sessionManager.GetSessionAsync(ChatApp.SessionId);
    chatApp.SetSession(session);

    var window = chatApp.BuildMainWindow();
    app.Run(window);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C / shutdown follows the same graceful path as /exit.
}
catch (Exception exception)
{
    Console.Error.WriteLine($"[startup.error] {exception.GetType().Name}: {exception.Message}");
}
finally
{
    shutdown.Cancel();
    await history.Clear();
    if (clockService != null)
    {
        await clockService.DisposeAsync();
    }
    foreach (var terminalToolSet in terminalToolSets)
    {
        terminalToolSet.Dispose();
    }
    browser?.Dispose();
    shutdown.Dispose();
}

return;

Task<(global::Agent.Agent, Action<string>)> CreateAgentAsync(string sessionId)
{
    var manager = sessionManager ?? throw new InvalidOperationException("会话管理器尚未初始化。");
    var clock = clockService ?? throw new InvalidOperationException("定时服务尚未初始化。");
    var webBrowser = browser ?? throw new InvalidOperationException("浏览器尚未初始化。");
    var terminal = new TerminalToolSet(manager, sessionId, initialWorkingDirectory: originalWorkingDirectory);
    terminalToolSets.Add(terminal);

    return CreateAsync();

    async Task<(global::Agent.Agent, Action<string>)> CreateAsync()
    {
        var agent = await global::Agent.Agent.Create(
            history,
            llmClient,
            128_000,
            new AgentOptions
            {
                SystemPrompt = """
                    You are a helpful interactive assistant. Use tools when they help answer the user.
                    You may execute terminal commands only when the user has explicitly authorized that action,
                    and only when the command is non-destructive. Do not delete, overwrite, move, install,
                    publish, or otherwise change user data or system state unless the user has explicitly
                    requested the exact action; ask for clarification when authorization or impact is unclear.
                    """,
                OnLog = chatApp.OnAgentLog,
            },
            [
                terminal,
                new Cron(sessionId, clock),
                new TimeToolSet(),
                new TodoListToolSet(),
                new SkillToolSet(skillsPath),
                new WebTools(webBrowser),
            ]);

        return (agent, response => chatApp.OnCronMessage(response));
    }
}

/// <summary>Process-only context storage for the fixed TUI session.</summary>
internal sealed class PlaceholderContextHistory : ContextHistory
{
    private readonly object sync = new();
    private List<Message> messages = [];

    public Task<IList<Message>> Restore()
    {
        lock (sync)
        {
            return Task.FromResult<IList<Message>>(messages.ToList());
        }
    }

    public Task Append(IList<Message> newMessages)
    {
        lock (sync)
        {
            messages = newMessages.ToList();
        }
        return Task.CompletedTask;
    }

    public Task Replace(IList<Message> newMessages) => Append(newMessages);

    public Task Clear()
    {
        lock (sync)
        {
            messages.Clear();
        }
        return Task.CompletedTask;
    }
}
