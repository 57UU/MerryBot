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

    private readonly string _shell;
    private readonly string _arguments;
    private readonly string _endMarker = $"_END_{Guid.NewGuid()}";
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private Process _process = null!;
    private StreamWriter _writer = null!;
    private StreamReader _reader = null!;
    private StreamReader _errorReader = null!;
    private bool _isGotoHome;

    /// <summary>
    /// 创建终端：user 非空时以 sudo -u user 运行 bash；user 为空直接运行 bash
    /// （Linux 用 /bin/bash，Windows 用 PATH 中的 bash，便于开发环境测试）。
    /// </summary>
    public static Terminal Create(string? user = null)
    {
        if (!string.IsNullOrEmpty(user))
        {
            return new Terminal("sudo", $"-u {user} /bin/bash");
        }
        string shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bash" : "/bin/bash";
        return new Terminal(shell, string.Empty);
    }

    public Terminal(string shell, string arguments)
    {
        _shell = shell;
        _arguments = arguments;
        InitializeProcess();
    }

    /// <summary>
    /// 执行命令并等待输出结束（以 marker 标记），返回合并后的 stdout/stderr。
    /// cwd 非空时先切换工作目录；timeoutSeconds 为 null 时不设超时，否则超时后终止并重启 shell。
    /// </summary>
    public async Task<string> RunCommandAsync(string command, string? cwd = null, int? timeoutSeconds = 30)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_isGotoHome)
            {
                await _writer.WriteLineAsync("cd ~");
                await _writer.FlushAsync();
                _isGotoHome = true;
            }
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                await _writer.WriteLineAsync($"cd {cwd}");
                await _writer.FlushAsync();
            }

            await _writer.WriteLineAsync(command);
            await _writer.WriteLineAsync($"printf '\\n{_endMarker}\\n';printf '\\n{_endMarker}\\n' >&2");
            await _writer.FlushAsync();

            using var cts = timeoutSeconds is > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
                : new CancellationTokenSource();
            var readOutTask = ReadUntilMarkerAsync(_reader, _endMarker, cts.Token);
            var readErrTask = ReadUntilMarkerAsync(_errorReader, _endMarker, cts.Token);
            await Task.WhenAll(readOutTask, readErrTask);

            var (outText, outCancelled) = await readOutTask;
            var (errText, errCancelled) = await readErrTask;
            bool cancelled = outCancelled || errCancelled;

            string output = string.IsNullOrWhiteSpace(errText) ? outText : $"{outText}\nerror:{errText}";
            output = output.Trim().Replace("\t", " ");
            if (string.IsNullOrWhiteSpace(output))
            {
                output = "[无输出]";
            }

            if (cancelled)
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
            return output;
        }
        catch (Exception e)
        {
            return $"Error:{e.Message}";
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>逐行读取输出，直到行尾 marker 或取消（超时）</summary>
    private static async Task<(string content, bool cancelled)> ReadUntilMarkerAsync(StreamReader reader, string endMarker, CancellationToken token)
    {
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync(token);
                if (line == null) break;
                if (line.Trim() == endMarker) break;
                sb.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            return (sb.ToString(), true);
        }
        return (sb.ToString(), false);
    }

    private async Task<bool> TryKillAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
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
            },
        };
        _process.Start();
        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;
        _errorReader = _process.StandardError;
        _isGotoHome = false;
        CreatedCount++;
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
        if (_process != null)
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
            _process.Dispose();
        }
    }
}
