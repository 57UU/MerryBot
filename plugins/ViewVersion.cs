using NapcatClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace BotPlugin;

[PluginTag("version", "使用 /version 来查看关于")]
public class ViewVersion : Plugin
{
    private string gitInfo;
    public ViewVersion(PluginInterop interop) : base(interop)
    {
        gitInfo= GetGitInfo();
        Logger.Info("version-view plugin start");
    }
    /// <summary>
    /// 执行 Git 命令并返回输出
    /// </summary>
    /// <param name="arguments">Git 命令参数</param>
    /// <returns>命令输出</returns>
    private static string ExecuteGitCommand(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    public static string GetGitInfo()
    {
        try
        {
            // 使用单个命令获取大部分信息
            string gitLogFormat = "--pretty=format:%H|%ci|%s";
            string logOutput = ExecuteGitCommand($"log -1 {gitLogFormat}");
            
            if (logOutput.StartsWith("Error:"))
                return $"获取Git信息失败: {logOutput}";
                
            string[] logParts = logOutput.Split('|');
            if (logParts.Length < 3)
                return "解析Git日志信息失败";
                
            string commitHash = logParts[0];
            string commitDate = logParts[1];
            string commitMessage = logParts[2];
            
            // 获取其他信息
            string commitCount = ExecuteGitCommand("rev-list --count HEAD");
            string userName = ExecuteGitCommand("config user.name");
            
            // 格式化返回信息
            StringBuilder gitInfo = new StringBuilder();
            //gitInfo.AppendLine($"Git信息:");
            gitInfo.AppendLine($"Message: {commitMessage}");
            gitInfo.AppendLine($"Date: {commitDate}");
            gitInfo.AppendLine($"Count: {commitCount}");
            gitInfo.AppendLine($"Commit: {commitHash.AsSpan(0, 12)}");
            gitInfo.AppendLine($"By: {userName}");

            return gitInfo.ToString();
        }
        catch (Exception ex)
        {
            throw new PluginNotUsableException($"获取Git信息失败: {ex.Message}");
        }
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/version"))
        {
            _ = Actions.SendGroupMessage(groupId, gitInfo);
        }
    }
}

