using System.Collections.Concurrent;

namespace BotPlugin;

/// <summary>
/// 管理异步 shell 任务，每个任务独立 Terminal 实例以支持并行执行
/// </summary>
public class ShellManager : IDisposable
{
    private readonly string _user;
    private readonly TimeSpan _maxAge;
    private readonly Terminal _syncTerminal;

    private record ShellTaskInfo(Terminal Terminal, Task<string> Task, DateTime StartTime);
    private readonly ConcurrentDictionary<string, ShellTaskInfo> _tasks = new();

    /// <summary>
    /// 默认超时时间（秒）
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// 同步调用默认超时（秒）
    /// </summary>
    public const int DefaultSyncTimeoutSeconds = 10;

    public ShellManager(string user = "merrybot", TimeSpan? maxTaskAge = null)
    {
        _user = user;
        _maxAge = maxTaskAge ?? TimeSpan.FromMinutes(5);
        _syncTerminal = Terminal.CreateUserTerminal(user);
    }

    /// <summary>
    /// 同步执行命令并等待结果（适用于短时任务）
    /// </summary>
    public async Task<string> RunSync(string command, int timeoutSeconds = DefaultSyncTimeoutSeconds)
    {
        var timeoutMs = timeoutSeconds * 1000 + 500;
        return await _syncTerminal.RunCommandAsync(
            command,
            timeoutMs: timeoutMs,
            useHardTimeout: true,
            waitMutex: true
        );
    }

    /// <summary>
    /// 异步启动 shell 命令，立即返回 task_id
    /// </summary>
    /// <param name="command">要执行的命令</param>
    /// <param name="timeoutSeconds">超时秒数，默认30s</param>
    public string StartCommand(string command, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        CleanupExpiredTasks();

        var taskId = Guid.NewGuid().ToString("N")[..8];
        Terminal? terminal = null;
        
        try
        {
            terminal = Terminal.CreateUserTerminal(_user);
            var timeoutMs = timeoutSeconds * 1000 + 500;

            var task = terminal.RunCommandAsync(
                command,
                timeoutMs: timeoutMs,
                useHardTimeout: true,
                waitMutex: true
            );

            _tasks[taskId] = new ShellTaskInfo(terminal, task, DateTime.Now);
            return taskId;
        }
        catch
        {
            terminal?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 查询任务结果
    /// </summary>
    /// <returns>(是否完成, 结果或状态提示)</returns>
    public async Task<(bool completed, string result)> QueryResult(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var info))
        {
            return (true, $"未找到任务 {taskId}，可能已过期或从未存在。");
        }

        if (!info.Task.IsCompleted)
        {
            var elapsed = (DateTime.Now - info.StartTime).TotalSeconds;
            return (false, $"任务 {taskId} 仍在执行中（已等待 {elapsed:F0}秒），请稍后再查询。");
        }

        _tasks.TryRemove(taskId, out _);
        info.Terminal.Dispose();
        var result = await info.Task;
        return (true, result);
    }

    /// <summary>
    /// 清理过期任务
    /// </summary>
    private void CleanupExpiredTasks()
    {
        foreach (var kvp in _tasks)
        {
            if (DateTime.Now - kvp.Value.StartTime > _maxAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var info))
                {
                    info.Terminal.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _tasks)
        {
            if (_tasks.TryRemove(kvp.Key, out var info))
            {
                info.Terminal.Dispose();
            }
        }
        _syncTerminal.Dispose();
    }
}
