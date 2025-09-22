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
        Logger.Info("about plugin start");
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
    async void handleCommand(string command,long groupId,long messageId,bool isAuthorized)
    {
        string result;
        result = await RunCommandAsync(command,false,timeout:1000);
        result = PluginUtils.ConstraintLength(result, 3000);

        await Actions.ChooseBestReplyMethod(groupId, messageId, result);
    }
    public async Task<string> RunCommandAsync(string command, bool isAuthorized, int timeout = 500)
    {
        // 根据操作系统选择合适的命令解释器
        string shell,arguments;
        if (OperatingSystem.IsLinux())
        {
            shell = "/bin/bash";
            arguments = "-i";
        }
        else
        {
            throw new PlatformNotSupportedException("不支持的操作系统");
        }

        // 创建进程启动信息
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = arguments,
            RedirectStandardInput= true,
            RedirectStandardOutput = true,   // 重定向标准输出
            RedirectStandardError = true,    // 重定向错误输出
            UseShellExecute = false,         // 不使用操作系统shell启动
            CreateNoWindow = true,           // 不创建新窗口
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        // 创建取消令牌源，设置超时
        using var cts = new CancellationTokenSource(timeout);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            // 启动进程
            process.Start();
            var input= process.StandardInput;
            if (!isAuthorized)
            {
                await input.WriteLineAsync("sudo su marrybot");
                await input.WriteLineAsync("""alias sudo='echo "Command not found." >&2; false'""");
            }
            await input.WriteLineAsync("cd ~");
            await input.WriteLineAsync(command);
            await input.WriteLineAsync("exit");
            if (!isAuthorized)
            {
                await input.WriteLineAsync("exit");
            }
            input.Close();

            // 异步读取输出和错误流
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            // 等待进程完成或超时
            var processTask = process.WaitForExitAsync(cts.Token);
            var completedTask = await Task.WhenAny(processTask, Task.Delay(Timeout.Infinite, cts.Token));

            // 如果超时或被取消，终止进程
            if (completedTask == processTask && processTask.IsCompletedSuccessfully)
            {
                // 等待所有输出读取完成
                string output = (await outputTask);
                string error = (await errorTask);

                if (string.IsNullOrWhiteSpace(output))
                {
                    output = "[No Output]";
                }

                // 合并输出和错误信息
                return string.IsNullOrEmpty(error) ? output : $"Error: {error}\n{output}";
            }
            else { 
                var errMsg = $"命令执行超时（超过{timeout}ms）并已被终止";
                Logger.Warn("errMsg");
                if (!process.HasExited)
                {
                    process.Kill(); // 强制终止进程
                }
                return errMsg;
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            return $"命令执行超时（超过{timeout}ms）并已被终止";
        }
        catch (Exception ex)
        {
            return $"执行命令时发生错误: {ex.Message}";
        }
    }
}
