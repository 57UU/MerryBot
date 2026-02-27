

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CommonLib;

namespace BotPlugin;

public partial class Terminal : IDisposable
{
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    private Process _process=null;
    private StreamWriter _writer = null;
    private StreamReader _reader = null;
    private StreamReader _errorReader = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。

    private readonly string _endMarker = $"_END_{Guid.NewGuid()}";
    private readonly SemaphoreSlim mutex = new(1);
    public ISimpleLogger logger=ConsoleLogger.Instance;

    bool isGotoHome = false;
    readonly string shell, arguments;
    public static Terminal CreateUserTerminal(string user="merrybot"){
        return new Terminal("sudo", $"-u {user} /bin/sh");
    }
    public Terminal(string shell, string arguments)
    {
        this.shell = shell;
        this.arguments = arguments;
        InitializeProcess();
        logger.Info("bash created");
    }
    private void InitializeProcess()
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        _process.Start();
        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;
        _errorReader = _process.StandardError;
        isGotoHome = false;
    }
    private void RestartProcess()
    {
        try
        {
            Dispose();
            InitializeProcess();
        }
        catch (Exception e)
        {
            logger.Error($"shell error:{e.Message}");
        }
    }
    public async Task<bool> IsBuiltinAsync(string command)
    {
        var result = await RunCommandAsync($"type -t {command}", timeoutMs: -1);
        logger.Trace($"test builtin result:{result}");
        return result == "builtin" || result == "keyword";
    }

    /// <summary>
    /// 运行命令并返回结果
    /// </summary>
    /// <param name="command">要执行的命令</param>
    /// <param name="timeoutMs">超时毫秒数</param>
    /// <param name="useSoftTimeout">是否使用软超时</param>
    /// <param name="useHardTimeout">是否使用硬超时</param>
    /// <param name="waitMutex">是否等待互斥锁</param>
    /// <returns>命令输出</returns>
    public async Task<string> RunCommandAsync(string command, int timeoutMs = 2000, bool useSoftTimeout=false,bool useHardTimeout=false,bool waitMutex=false)
    {
        if (!isGotoHome)
        {
            await _writer.WriteLineAsync("cd ~");
            await _writer.FlushAsync();
            isGotoHome = true;
        }
        if(waitMutex){
            await mutex.WaitAsync();
        }else{
            if(!mutex.Wait(0)){
                return "请等待上一个命令执行";
            }
        }

        // 用 Linux 的 timeout 包装
        float sec = timeoutMs / 1000.0f;

        string fullCommand;
        if (useSoftTimeout)
        {
            fullCommand = $"timeout -k 0.5s {sec}s {command}|| ([ $? -eq 124 ] && echo \"timeout:{sec}s\";); ";
        }
        else
        {
            fullCommand = command;
        }
        //fullCommand = $"{fullCommand}echo -e '\\n{_endMarker}\\n';echo -e '\\n{_endMarker}\\n' >&2";

        logger.Trace($"CMD: {fullCommand}");
        await _writer.WriteLineAsync(fullCommand);
        await _writer.WriteLineAsync($"printf '\\n{_endMarker}\\n';printf '\\n{_endMarker}\\n' >&2");
        await _writer.FlushAsync();

        var ctsToken = new CancellationTokenSource();
        if (useHardTimeout)
        {
            ctsToken.CancelAfter(timeoutMs+500);
        }

        try
        {
            var readStandardOutTask = _readOutput(_reader, _endMarker,ctsToken.Token)!;
            var readErrorTask = _readOutput(_errorReader, _endMarker,ctsToken.Token)!;
            await Task.WhenAll(readStandardOutTask, readErrorTask);

            var (_standardOutTrim,cancelled) = readStandardOutTask.Result;
            var (_errTrim,cancelled2) = readErrorTask.Result;
            _standardOutTrim = _standardOutTrim.Trim();
            _errTrim = _errTrim.Trim();

            cancelled = cancelled || cancelled2;

            string output;
            if (string.IsNullOrWhiteSpace(_errTrim))
            {
                //no error
                output = _standardOutTrim;
            }
            else
            {
                output = $"{_standardOutTrim}\nerror:{_errTrim}";
            }
            output = output.Trim().Replace("\t", " ");
            if (string.IsNullOrWhiteSpace(output))
            {
                output= "[无输出]";
            }
            if(cancelled){
                if(await TryKillProcessAsync()){
                    output +="\n命令执行时间过长，终止shell";
                }
                else{
                    output +="\n命令执行时间过长，终止shell失败";
                }
            }
            if (_process.HasExited)
            {
                RestartProcess();
                output +="\nProcess Exited. Restarting...";
            }
            return output;
        }
        catch (Exception e) {
            return $"Error:{e.Message}";
        }
        finally
        {
            mutex.Release();
        }
    }
    private async Task<bool> TryKillProcessAsync()
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
        catch (Exception ex)
        {
            logger.Error($"bash exit failed {ex.Message}");
            return false;
        }
    }
    private static async Task<(string content,bool isCancelled)> _readOutput(StreamReader reader,string endMarker,CancellationToken token)
    {
        var sb = new StringBuilder();
        try{
            while (true)
            {
                string? line = await reader.ReadLineAsync(token);
                //logger.Info($"line received: {line}");
                if (line == null) break;

                if (line.Trim() == endMarker)
                {
                    //logger.Info("end reached");
                    break;
                }


                sb.AppendLine(line);
            }
        }catch(OperationCanceledException)
        {
            return (sb.ToString(),true);
        }
        return (sb.ToString(),false);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }
        _process.Dispose();
        GC.SuppressFinalize(this);
    }
}