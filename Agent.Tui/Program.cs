using System.Collections.Concurrent;
using System.Text;
using Agent;
using Agent.Session;
using Agent.Tools;
using BrowserService;
using LlmBackend;
using LlmClient;

const string SessionId = "agent-tui-default";
const string OpenCodeBaseUrl = "https://opencode.ai/zen/go/v1";
const string Model = "deepseek-v4-flash";
const int ContextTokenLimit = 128_000;

var consoleLock = new object();
var shutdown = new CancellationTokenSource();
var debugEnabled = 1;

void Write(string text)
{
    lock (consoleLock)
    {
        Console.Write(text);
    }
}

void WriteLine(string text = "")
{
    lock (consoleLock)
    {
        Console.WriteLine(text);
    }
}

bool IsDebugEnabled() => Volatile.Read(ref debugEnabled) == 1;

bool ToggleDebug()
{
    while (true)
    {
        var current = Volatile.Read(ref debugEnabled);
        var next = current == 0 ? 1 : 0;
        if (Interlocked.CompareExchange(ref debugEnabled, next, current) == current)
        {
            return next == 1;
        }
    }
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

WriteLine("Agent.Tui — OpenCode Go / deepseek-v4-flash");
var apiKey = ReadApiKey(Write, WriteLine);
if (string.IsNullOrWhiteSpace(apiKey))
{
    WriteLine("未输入 API Key，已退出。");
    shutdown.Dispose();
    return;
}

// Browser's bundled scripts are copied beside the executable.  Keeping the
// process working directory here makes `dotnet run --project Agent.Tui` work
// consistently from the solution root as well.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var skillsPath = Path.Combine(AppContext.BaseDirectory, "skills");
Directory.CreateDirectory(skillsPath);

var client = new Client(
    new ChatCompletionBackend(OpenCodeBaseUrl, apiKey, Model),
    new ClientConfig(3, TimeSpan.FromSeconds(1)));
var history = new PlaceholderContextHistory();
var terminalToolSets = new ConcurrentBag<TerminalToolSet>();
Browser? browser = null;
ClockService? clockService = null;
AgentSessionManager? sessionManager = null;

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
        new AgentSessionClockExecutor(sessionManager));
    await clockService.StartAsync(shutdown.Token);

    var session = await sessionManager.GetSessionAsync(SessionId);
    WriteLine("已就绪。输入 /debug 切换调试日志；输入 /exit 或发送 EOF 退出。");

    while (!shutdown.IsCancellationRequested)
    {
        Write("\nYou> ");
        string? input;
        try
        {
            input = await Console.In.ReadLineAsync(shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            break;
        }

        if (input == null || string.Equals(input.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
        if (string.Equals(input.Trim(), "/debug", StringComparison.OrdinalIgnoreCase))
        {
            WriteLine(ToggleDebug()
                ? "[debug] 调试日志已开启。"
                : "[debug] 调试日志已关闭。最终回复和错误仍会显示。");
            continue;
        }
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        try
        {
            await session.ChatAndWaitAsync(
                input,
                response => WriteLine($"\nAssistant> {response}"),
                shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            WriteLine($"[agent.error] {exception.Message}");
        }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Ctrl+C follows the same graceful-shutdown path as /exit.
}
catch (Exception exception)
{
    WriteLine($"[startup.error] {exception.Message}");
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

Task<(global::Agent.Agent, Action<string>)> CreateAgentAsync(string sessionId)
{
    var manager = sessionManager ?? throw new InvalidOperationException("会话管理器尚未初始化。");
    var clock = clockService ?? throw new InvalidOperationException("定时服务尚未初始化。");
    var webBrowser = browser ?? throw new InvalidOperationException("浏览器尚未初始化。");
    var terminal = new TerminalToolSet(manager, sessionId);
    terminalToolSets.Add(terminal);

    return CreateAsync();

    async Task<(global::Agent.Agent, Action<string>)> CreateAsync()
    {
        var agent = await global::Agent.Agent.Create(
            history,
            client,
            ContextTokenLimit,
            new AgentOptions
            {
                SystemPrompt = """
                    You are a helpful interactive assistant. Use tools when they help answer the user.
                    You may execute terminal commands only when the user has explicitly authorized that action,
                    and only when the command is non-destructive. Do not delete, overwrite, move, install,
                    publish, or otherwise change user data or system state unless the user has explicitly
                    requested the exact action; ask for clarification when authorization or impact is unclear.
                    """,
                OnLog = eventInfo =>
                {
                    if (IsDebugEnabled())
                    {
                        WriteLine(FormatLogEvent(eventInfo));
                    }
                },
            },
            [
                terminal,
                new Cron(sessionId, clock),
                new TimeToolSet(),
                new TodoListToolSet(),
                new SkillToolSet(skillsPath),
                new WebTools(webBrowser),
            ]);

        return (agent, response => WriteLine($"\n[Cron]\n{response}"));
    }
}

static string ReadApiKey(Action<string> write, Action<string> writeLine)
{
    write("OpenCode Go API Key: ");
    if (Console.IsInputRedirected)
    {
        var redirectedValue = Console.ReadLine() ?? string.Empty;
        writeLine(string.Empty);
        return redirectedValue.Trim();
    }

    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            break;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0)
            {
                value.Length--;
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
        }
    }
    writeLine(string.Empty);
    return value.ToString().Trim();
}

static string FormatLogEvent(AgentLogEvent eventInfo)
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

static string ToolLabel(AgentLogEvent eventInfo) =>
    string.IsNullOrWhiteSpace(eventInfo.ToolCallId)
        ? eventInfo.ToolName ?? "unknown"
        : $"{eventInfo.ToolName ?? "unknown"} id={eventInfo.ToolCallId}";

static string FormatUsage(TokenUsage? usage) => usage == null
    ? string.Empty
    : $"usage={usage.totalUsage} (input={usage.promptUsage}, output={usage.completionUsage}, cached={usage.cachedUsage})";

static string Truncate(string? value, int maximumLength = 1000)
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
