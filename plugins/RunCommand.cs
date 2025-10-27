﻿using CommonLib;
using NapcatClient;
using OpenQA.Selenium;
using OpenQA.Selenium.BiDi.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("shell", "使用 /sh 运行终端命令")]
public class RunCommand : Plugin
{
    long authorized;
    bool useUnprivileged = true;
    public RunCommand(PluginInterop interop) : base(interop)
    {
        //not Linux 
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PluginNotUsableException("shell plugin can only support Linux");
        }
        terminal = new();
        terminal.logger = Logger;
        authorized=interop.AuthorizedUser;
        Logger.Info("shell plugin started");
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        long sender=data.sender.user_id;
        var isAuthorized = sender == authorized;
        if (useUnprivileged==false && !isAuthorized)
        {
            _ = Actions.SendGroupMessage(groupId, "401 Unauthorized\nYou do not have the permission");
            return;
        }
        if (IsStartsWith(chain, "/sh"))
        {
            var text = (chain[0].Data["text"] as string)!.Trim();
            //rm first /sh
            var first = text.IndexOf(' ');
            if (first == -1)
            {
                _ = Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            text = text[first..];
            if (text.Length == 0)
            {
                _ = Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            if (text[0] == ' ')
            {
                text = text[1..];
            }
            _=HandleCommand(text, groupId,data.message_id,isAuthorized);
        }
    }
    internal Terminal terminal ;
    async Task HandleCommand(string command,long groupId,long messageId,bool isAuthorized=false)
    {
        string result;
        try
        {
            result = await terminal.RunCommandAutoTimeoutAsync(command, timeoutMs: 2000);
        }
        catch (Exception e) { 
            result = $"error:{e.Message}";
        }
        
        result = PluginUtils.ConstraintLength(result, 3000);

        await Actions.ChooseBestReplyMethod(groupId, messageId, result);
    }

}


public partial class Terminal : IDisposable
{
    private Process _process=null;
    private StreamWriter _writer=null;
    private StreamReader _reader=null;
    private StreamReader _errorReader=null;
    private readonly string _endMarker = "__END__";
    private readonly SemaphoreSlim mutex = new(1);
    public ISimpleLogger logger=ConsoleLogger.Instance;

    bool isGotoHome = false;
    readonly string shell, arguments;
    public Terminal(string shell = "sudo", string arguments = "-u merrybot /bin/bash")
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
        var result = await RunCommandAsync($"type -t {command}"
            ,false, timeoutMs: -1);
        logger.Trace($"test builtin result:{result}");
        return result == "builtin" || result == "keyword";
    }
    public async Task<string> RunCommandAutoTimeoutAsync(string command, int timeoutMs = 2000)
    {
        if (IsContainMultipleCommands(command))
        {
            return "仅支持使用;'连接多条指令";
        }
        StringBuilder sb = new();
        var commands = SplitCommands(command);
        foreach(var c in commands)
        {

            var isBuiltin = await IsBuiltinAsync(c);
            var useTimeout = !isBuiltin;
            logger.Info($"type is builtin? {isBuiltin}");
            var result1= await RunCommandAsync(c, useTimeout, timeoutMs);
            sb.AppendLine(result1.Replace("\n"," "));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 运行命令并返回结果
    /// </summary>
    /// <param name="command">要执行的命令</param>
    /// <param name="timeoutMs">超时毫秒数</param>
    /// <returns>命令输出</returns>
    public async Task<string> RunCommandAsync(string command,bool useTimeout, int timeoutMs = 2000)
    {
        if (!isGotoHome)
        {
            await _writer.WriteLineAsync("cd ~");
            await _writer.FlushAsync();
            isGotoHome = true;
        }
        if (mutex.CurrentCount < 1)
        {
            return "请等待上一个命令执行";
        }
        await mutex.WaitAsync();
        string marker = $"{_endMarker}_{Guid.NewGuid()}";

        // 用 Linux 的 timeout 包装
        float sec = timeoutMs / 1000.0f;

        string fullCommand;
        if (useTimeout)
        {
            fullCommand = $"timeout -k 0.5s {sec}s {command}|| ([ $? -eq 124 ] && echo \"timeout:{sec}s\";); ";
        }
        else
        {
            fullCommand = $"{command};";
        }
        fullCommand = $"{fullCommand}echo -e '\\n{marker}\\n';echo -e '\\n{marker}\\n' >&2";

        logger.Trace($"CMD: {fullCommand}");
        await _writer.WriteLineAsync(fullCommand);
        await _writer.FlushAsync();


        try
        {
            var readStandardOutTask = _readOutput(_reader, marker)!;
            var readErrorTask = _readOutput(_errorReader, marker)!;
            await Task.WhenAll(readStandardOutTask, readErrorTask);

            var _standardOutTrim = readStandardOutTask.Result!.Trim();
            var _errTrim = readErrorTask.Result!.Trim();
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
    private static async Task<string> _readOutput(StreamReader reader,string endMarker)
    {
        var sb = new StringBuilder();
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            //logger.Info($"line received: {line}");
            if (line == null) break;

            if (line.Trim() == endMarker)
            {
                //logger.Info("end reached");
                break;
            }


            sb.AppendLine(line);
        }
        return sb.ToString();
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
    public static string EscapeForShell(string input)
    {
        if (input == null)
            return "''"; // null 当成空字符串

        // 单引号内的单引号需要特殊处理
        return "'" + input.Replace("'", "'\\''") + "'";
    }
    public static bool IsContainMultipleCommands(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 找到第一个分隔符
        var match = _splitRegex().Match(input);
        return match.Success;
    }
    public static List<string> SplitCommands(string input)
    {
        List<string> commands = new List<string>();
        StringBuilder currentCommand = new StringBuilder();
        bool inSingleQuotes = false;
        bool inDoubleQuotes = false;
        bool escaped = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (escaped)
            {
                // 处理转义字符
                currentCommand.Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    // 遇到转义符号，标记下一个字符为转义状态
                    currentCommand.Append(c);
                    escaped = true;
                    break;
                case '\'':
                    if (!inDoubleQuotes)
                    {
                        inSingleQuotes = !inSingleQuotes;
                    }
                    currentCommand.Append(c);
                    break;
                case '\"':
                    if (!inSingleQuotes)
                    {
                        inDoubleQuotes = !inDoubleQuotes;
                    }
                    currentCommand.Append(c);
                    break;
                case ';':
                    // 只有在不在引号内时，分号才作为命令分隔符
                    if (!inSingleQuotes && !inDoubleQuotes)
                    {
                        commands.Add(currentCommand.ToString().Trim());
                        currentCommand.Clear();
                    }
                    else
                    {
                        currentCommand.Append(c);
                    }
                    break;
                default:
                    currentCommand.Append(c);
                    break;
            }
        }

        // 添加最后一个命令
        string lastCommand = currentCommand.ToString().Trim();
        if (!string.IsNullOrEmpty(lastCommand))
        {
            commands.Add(lastCommand);
        }

        return commands;
    }

    [GeneratedRegex(@"(\|\||&&)")]
    private static partial Regex _splitRegex();
}