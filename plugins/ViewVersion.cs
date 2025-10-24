using NapcatClient;
using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace BotPlugin;

[PluginTag("version", "/version查看当前版本;/update更新软件",priority:114514)]
public class ViewVersion : Plugin
{
    private string gitInfo;
    private long authorized;
#pragma warning disable CS8625
    //data will be loaded in `OnLoaded` function
    private Data data=null;
#pragma warning restore CS8625
    public ViewVersion(PluginInterop interop) : base(interop)
    {
        gitInfo= GetGitInfo().Result.Trim();
        authorized = interop.AuthorizedUser;
        if (authorized < 0)
        {
            Logger.Warn("authorized-user is not valid, '/update' will be disabled");
        }
        Logger.Info("version-view plugin start");
    }
    public async override Task OnLoaded()
    {
        data=await Interop.PluginStorage.Load<Data>(new Data());
        Logger.Debug("data loaded");
        //if  contains update flag, then reply update info
        if (data.UpdateByGroupId > 0)
        {
            await Actions.SendGroupMessage(data.UpdateByGroupId, $"update successful\n{gitInfo}");
            data.UpdateByGroupId = -1;
            await Interop.PluginStorage.Save(data);
        }

    }
    /// <summary>
    /// 执行 Git 命令并返回输出
    /// </summary>
    /// <param name="arguments">Git 命令参数</param>
    /// <returns>命令输出</returns>
    private static async Task<string> ExecuteGitCommand(string arguments)
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
        string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        return output;
    }

    public static async Task<string> GetGitInfo()
    {
        try
        {
            // 使用单个命令获取大部分信息
            string gitLogFormat = "--pretty=format:%H|%ci|%s";
            string logOutput = await ExecuteGitCommand($"log -1 {gitLogFormat}");
            
            if (logOutput.StartsWith("Error:"))
                return $"获取Git信息失败: {logOutput}";
                
            string[] logParts = logOutput.Split('|');
            if (logParts.Length < 3)
                return "解析Git日志信息失败";
                
            string commitHash = logParts[0];
            string commitDate = logParts[1];
            string commitMessage = logParts[2];
            
            // 获取其他信息
            string commitCount = await ExecuteGitCommand("rev-list --count HEAD");
            string userName = await ExecuteGitCommand("config user.name");
            
            // 格式化返回信息
            StringBuilder gitInfo = new StringBuilder();
            //gitInfo.AppendLine($"Git信息:");
            gitInfo.AppendLine($"Change: {commitMessage}");
            gitInfo.AppendLine($"Date: {commitDate}");
            gitInfo.AppendLine($"Count: {commitCount}");
            gitInfo.AppendLine($"Commit: {commitHash.AsSpan(0, 12)}");
            if (!string.IsNullOrWhiteSpace(userName))
            {
                gitInfo.AppendLine($"By: {userName}");
            }

            return gitInfo.ToString();
        }
        catch (Exception ex)
        {
            throw new PluginNotUsableException($"获取Git信息失败: {ex.Message}");
        }
    }
    private static async Task<(string diff, string commitMessages)> GitFetchMerge()
    {
        await ExecuteGitCommand("fetch");
        var diff = await ExecuteGitCommand("merge");
        
        // 获取merge的commit messages
        string commitMessages;
        try
        {
            // 获取最近一次merge操作引入的所有commit
            // 使用 ORIG_HEAD 可以获取merge前的HEAD位置
            string mergeCommits = await ExecuteGitCommand("log ORIG_HEAD..HEAD --pretty=format:%s");
            
            if (string.IsNullOrWhiteSpace(mergeCommits))
            {
                // 如果ORIG_HEAD不可用，尝试获取最近5个commit
                mergeCommits = await ExecuteGitCommand("log -5 --pretty=format:%s");
            }
            
            if (string.IsNullOrWhiteSpace(mergeCommits))
            {
                commitMessages = "无法获取提交信息";
            }
            else
            {
                // 将多行提交信息格式化为更易读的形式
                var lines = mergeCommits.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 1)
                {
                    commitMessages = lines[0];
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"合并了 {lines.Length} 个提交:");
                    for (int i = 0; i < lines.Length; i++)
                    {
                        sb.AppendLine($"{i + 1}. {lines[i]}");
                    }
                    commitMessages = sb.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            commitMessages = $"获取提交信息时出错: {ex.Message}";
        }
        
        return (diff, commitMessages);
    }
    private async Task Update(long groupId)
    {
        var (diff, commitMessages) = await GitFetchMerge();
        diff = diff.Replace("+", "").Replace("-", "").Trim();
        await Actions.SendGroupMessage(groupId, $"{diff}\n{commitMessages}\nrestarting...");
        //store the update info
        data.UpdateByGroupId = groupId;
        await Interop.PluginStorage.Save(data);
        Interop.Shutdown(CommonLib.ExitCode.RESTART);
        
    } 
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/version"))
        {
            _ = Actions.SendGroupMessage(groupId, gitInfo);
        }else if (IsStartsWith(chain, "/update")){
            if (authorized == data.sender.user_id)
            {
                _ = Update(groupId);
            }
            else
            {
                _ = Actions.SendGroupMessage(groupId, "401 Unauthorized\nPermission Denied");
            }
        }
    }
    class Data
    {
        public long UpdateByGroupId=-1;
    }
}
