using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Session;

/// <summary>
/// 常驻 bash 进程封装（参考 plugins/RunCommand.Terminal.cs 精简实现）。
/// 命令通过 stdin 写入、以行尾 marker 标记输出结束；命令在进程内串行执行，
/// 多次调用间保留 shell 状态（如 cd 后的工作目录）。
/// 进程在构造时启动；如需"用到才加载"，由调用方以 Lazy 包住本类。
/// </summary>
public class Terminal : IDisposable
{
    /// <summary>已创建的进程实例数（含超时重启），用于懒加载验证与监控</summary>
    public static int CreatedCount { get; private set; }

    /// <summary>单次命令输出累积上限（字节），超过后停止累积并标记截断，防止 yes 类命令撑爆内存</summary>
    private const int MaxOutputBytes = 2 * 1024 * 1024;

    private readonly string _shell;
    private readonly string _arguments;
    private readonly string? _initialWorkingDirectory;
    private readonly string _endMarker = $"_END_{Guid.NewGuid()}";
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private Process _process = null!;
    private StreamWriter _writer = null!;
    private StreamReader _reader = null!;
    private StreamReader _errorReader = null!;
    private bool _disposed;

    /// <summary>
    /// 创建终端：user 非空时以 sudo -u user 运行 /bin/bash（忽略 shell 参数）；
    /// shell 为必填项，指定要启动的 shell 可执行文件（如 /bin/bash、bash、pwsh、cmd 等）。
    /// workingDirectory 指定 shell 进程的初始工作目录（不传则继承进程 CWD）。
    /// </summary>
    public static Terminal Create(string shell, string? user = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(shell))
        {
            throw new ArgumentException("shell 不能为空", nameof(shell));
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !string.IsNullOrEmpty(user))
        {
            return new Terminal("sudo", $"-u {user} /bin/bash", workingDirectory);
        }
        return new Terminal(shell, string.Empty, workingDirectory);
    }

    public Terminal(string shell, string arguments, string? workingDirectory = null)
    {
        _shell = shell;
        _arguments = arguments;
        _initialWorkingDirectory = workingDirectory;
        InitializeProcess();
    }

    /// <summary>
    /// 执行命令并等待输出结束（以 marker 标记），返回合并后的 stdout/stderr。
    /// cwd 非空时先切换工作目录；timeoutSeconds 为 null 时不设超时，否则超时后终止并重启 shell；
    /// backgroundOnTimeout 为 true 时超时不终止命令，而是转为后台继续运行（见 <see cref="TerminalRunResult.Detached"/>）。
    /// </summary>
    public async Task<TerminalRunResult> RunCommandAsync(string command, string? cwd = null, int? timeoutSeconds = 30, bool backgroundOnTimeout = false)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                await _writer.WriteLineAsync($"cd {ShellQuote(cwd)}");
                await _writer.FlushAsync();
            }

            await _writer.WriteLineAsync(command);
            await _writer.WriteLineAsync($"printf '\\n{_endMarker}\\n';printf '\\n{_endMarker}\\n' >&2");
            await _writer.FlushAsync();

            // 读取任务不带取消令牌：中途取消会破坏 StreamReader 的内部读状态（缓冲丢失、后续读提前 EOF），
            // 超时通过与读取任务竞争（WhenAny + Delay）实现；放弃前台等待时要么杀进程让流关闭收尾，
            // 要么（转后台）让同一批读取任务继续在后台跑到 marker 或进程退出。
            var readOutTask = ReadUntilMarkerAsync(_reader, _endMarker);
            var readErrTask = ReadUntilMarkerAsync(_errorReader, _endMarker);
            var allRead = Task.WhenAll(readOutTask, readErrTask);

            int timeoutMilliseconds = timeoutSeconds is > 0 ? timeoutSeconds.Value * 1000 : Timeout.Infinite;
            bool timedOut = await Task.WhenAny(allRead, Task.Delay(timeoutMilliseconds)) != allRead;

            if (timedOut && backgroundOnTimeout)
            {
                // 超时转后台：不杀进程，同一批读取任务继续在后台执行；
                // 必须先捕获旧进程再 Restart——RestartProcess 会原地替换本实例的进程与流字段。
                var oldProcess = _process;
                async Task<string> AssembleAsync()
                {
                    var texts = await allRead;
                    return MergeOutput(texts[0], texts[1]);
                }
                var detached = new TerminalDetachedCommand(oldProcess, AssembleAsync());
                // 只初始化新进程、不 Dispose 旧实例——RestartProcess 的 Dispose 会把旧进程（即后台任务
                // 正在使用的进程）一并杀死；旧进程由 detached 句柄持有并负责最终回收
                try
                {
                    InitializeProcess();
                }
                catch
                {
                    // 重启失败：保留原状态，下次调用会再次尝试
                }
                return new TerminalRunResult
                {
                    Output = "命令执行超时，已自动转入后台继续运行",
                    Detached = detached,
                };
            }

            // 超时路径必须先杀进程：流随之关闭，allRead 才能结束并带回已收到的部分输出
            var texts = await allRead;
            string output = MergeOutput(texts[0], texts[1]);

            if (timedOut)
            {
                bool killed = await TryKillAsync();
                RestartProcess();
                output += killed
                    ? "\n命令执行超时，已终止并重启 shell"
                    : "\n命令执行超时，终止 shell 失败";
            }
            else if (_process.HasExited)
            {
                RestartProcess();
                output += "\nShell 进程已退出，已重启";
            }
            return new TerminalRunResult { Output = output };
        }
        catch (Exception e)
        {
            return new TerminalRunResult { Output = $"Error:{e.Message}" };
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>合并 stdout/stderr 并做基础清理（去首尾空白、制表符转空格；全空时返回占位文本）</summary>
    private static string MergeOutput(string outText, string errText)
    {
        string output = string.IsNullOrWhiteSpace(errText) ? outText : $"{outText}\nerror:{errText}";
        output = output.Trim().Replace("\t", " ");
        return string.IsNullOrWhiteSpace(output) ? "[无输出]" : output;
    }

    /// <summary>用单引号包裹并转义，使含空格/特殊字符的路径安全传给 shell</summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>逐行读取输出，直到行尾 marker 或流关闭；累积超过字节上限后停止累积并标记截断。
    /// 不接收取消令牌——中途取消会破坏 StreamReader 内部读状态，调用方通过杀进程（流关闭自然结束）收尾</summary>
    private static async Task<string> ReadUntilMarkerAsync(StreamReader reader, string endMarker)
    {
        var sb = new StringBuilder();
        long byteCount = 0;
        bool truncated = false;
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null) break;
            if (line.Trim() == endMarker) break;
            if (truncated) continue; // 超限后继续排空流（直到 marker），但不再累积
            byteCount += Encoding.UTF8.GetByteCount(line) + 2; // 含换行
            if (byteCount > MaxOutputBytes)
            {
                truncated = true;
                sb.AppendLine($"\n…（输出超过 {MaxOutputBytes / (1024 * 1024)}MB，已截断）");
                continue;
            }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private async Task<bool> TryKillAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                // 终止整个进程树，确保 bash/sudo 派生的子进程一并终止（Windows 同样生效）
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void InitializeProcess()
    {
        // 事务式初始化：先保存原进程与流字段，失败时恢复并回收半成品新进程，
        // 避免 _process 指向新进程、流仍指向旧进程的不一致状态（下次调用会再次尝试）。
        // 注意不能在此 Dispose 旧进程——转后台路径中旧进程仍被后台任务使用。
        var oldProcess = _process;
        var oldWriter = _writer;
        var oldReader = _reader;
        var oldErrorReader = _errorReader;
        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _shell,
                    Arguments = _arguments,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = !string.IsNullOrWhiteSpace(_initialWorkingDirectory)
                        ? _initialWorkingDirectory
                        : Environment.CurrentDirectory,
                },
            };
            _process.Start();
            _writer = _process.StandardInput;
            _reader = _process.StandardOutput;
            _errorReader = _process.StandardError;
            _disposed = false;
            CreatedCount++;
        }
        catch
        {
            // 回收可能已创建的新进程（Start 失败时 HasExited 可能不可查询，需单独兜底）
            try
            {
                if (_process is not null && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process?.Dispose();
            }
            catch
            {
                // 进程可能未成功启动，忽略清理异常
            }
            _process = oldProcess;
            _writer = oldWriter;
            _reader = oldReader;
            _errorReader = oldErrorReader;
            throw;
        }
    }

    private void RestartProcess()
    {
        try
        {
            Dispose();
            InitializeProcess();
        }
        catch
        {
            // 重启失败：保留原对象，下次调用会再次尝试
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return; // 幂等：二次 Dispose 不再重复终止
        }
        _disposed = true;
        try
        {
            if (_process != null && !_process.HasExited)
            {
                // 终止整个进程树，确保子进程一并终止
                _process.Kill(entireProcessTree: true);
            }
            _process?.Dispose();
        }
        catch
        {
            // 进程可能已退出或已被释放，忽略
        }
    }
}

/// <summary>RunCommandAsync 的返回：Output 为给调用方的文本；Detached 非空表示前台超时已自动转后台</summary>
public sealed class TerminalRunResult
{
    public required string Output { get; init; }

    /// <summary>超时转后台后的续接句柄；null 表示未发生转后台</summary>
    public TerminalDetachedCommand? Detached { get; init; }
}

/// <summary>
/// 前台超时后转入后台继续执行的命令句柄：Completion 在命令真正结束时完成，返回全量输出
/// （含前台阶段已收到的部分输出）；Dispose 终止原进程树（幂等）。
/// 直接持有旧 Process 而非 Terminal 实例——Terminal 超时重启会原地替换其内部进程与流字段，
/// 持有实例引用反而会误杀重启后的新进程。
/// </summary>
public sealed class TerminalDetachedCommand : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public TerminalDetachedCommand(Process process, Task<string> completion)
    {
        _process = process;
        Completion = completion;
    }

    /// <summary>命令最终完成的任务：跑完或进程退出时结束</summary>
    public Task<string> Completion { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                // 终止整个进程树，确保子进程一并终止（Windows 同样生效）
                _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }
        catch
        {
            // 进程可能已退出或已被释放，忽略
        }
    }
}
