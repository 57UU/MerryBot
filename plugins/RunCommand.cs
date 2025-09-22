using NapcatClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebSocketSharp;

namespace BotPlugin;

[PluginTag("shell", "使用 /sh 运行终端命令")]
public class RunCommand : Plugin
{
    long authorized;
    bool useUnprivileged = true;
    public RunCommand(PluginInterop interop) : base(interop)
    {
        
        var tmp = interop.GetLongVariable("authorized-user");
        if (tmp == null)
        {
            Logger.Error("authorized is null, please specify 'authorized-user' parameter in setting.json/variables");
            IsEnable = false;
        }
        else
        {
            authorized = tmp.Value;
        }
        Logger.Info("shell plugin started");
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        long sender=data.sender.user_id;
        var isAuthorized = sender == authorized;
        if (useUnprivileged==false && !isAuthorized)
        {
            Actions.SendGroupMessage(groupId, "401 Unauthorized\nYou do not have the permission");
            return;
        }
        if (IsStartsWith(chain, "/sh"))
        {
            var text = (chain[0].Data["text"] as string).Trim();
            //rm first /sh
            var fisrt = text.IndexOf(" ");
            if (fisrt == -1)
            {
                Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            text = text.Substring(fisrt);
            if (text.Length == 0)
            {
                Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            if (text[0] == ' ')
            {
                text = text.Substring(1);
            }
            handleCommand(text, groupId,data.message_id,isAuthorized);
        }
    }
    Terminal terminal = new();
    async void handleCommand(string command,long groupId,long messageId,bool isAuthorized)
    {
        string result;
        result = await terminal.RunCommandAsync(command,timeoutMs:1000);
        result = PluginUtils.ConstraintLength(result, 3000);

        await Actions.ChooseBestReplyMethod(groupId, messageId, result);
    }

}


public class Terminal : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly StreamReader _errorReader;
    private readonly string _endMarker = "__END__";
    private readonly SemaphoreSlim mutex = new(1);

    public Terminal(string shell = "sudo", string arguments = "-u marrybot /bin/bash")
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments=arguments,
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
    }

    /// <summary>
    /// 运行命令并返回结果
    /// </summary>
    /// <param name="command">要执行的命令</param>
    /// <param name="timeoutMs">超时毫秒数</param>
    /// <returns>命令输出</returns>
    public async Task<string> RunCommandAsync(string command, int timeoutMs = 1000)
    {
        if (mutex.CurrentCount < 1)
        {
            return "请等待上一个命令执行";
        }
        await mutex.WaitAsync();
        string marker = $"{_endMarker}_{Guid.NewGuid()}";

        // 用 Linux 的 timeout 包装
        float sec = timeoutMs / 1000.0f;

        string fullCommand = $"timeout -k 0.5s {sec}s {command}|| [ $? -eq 124 ] && echo \"timeout:{sec}s\"; echo -e '\\n'; echo -e '{marker}\\n'";
        await _writer.WriteLineAsync(fullCommand);
        await _writer.FlushAsync();

        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(cts.Token);
                if (line == null) break;

                if (line == marker)
                    break;

                sb.AppendLine(line);
            }
            var error = await _errorReader.ReadToEndAsync();
            var output = string.IsNullOrWhiteSpace(sb.ToString()) ? "[no output]" : sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(error))
            {
                return output;
            }
            else
            {
                return $"{error}\n{output}";
            }
        }
        catch (OperationCanceledException)
        {
            sb.AppendLine($"Command timed out after {timeoutMs} ms.");
            return sb.ToString();
        }
        catch (Exception e) {
            return $"Error:{e.Message}";
        }
        finally
        {
            mutex.Release();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }
        _process.Dispose();
    }
}