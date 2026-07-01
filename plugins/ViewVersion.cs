using NapcatClient;
using System.Diagnostics;
using System.Text;

namespace BotPlugin;

[PluginTag("view-version", "版本查看", "/version查看当前版本;/update [-f]更新软件;/reload重启程序", priority: 114514)]
public partial class ViewVersion : Plugin
{
    private string gitInfo;
    private long authorized;
#pragma warning disable CS8625
    //data will be loaded in `OnLoaded` function
    private Data data = null;
#pragma warning restore CS8625
    public ViewVersion(PluginInterop interop) : base(interop)
    {
        gitInfo = GetGitInfo().Result.Trim();
        authorized = interop.AuthorizedUser;
        if (authorized < 0)
        {
            Logger.Warn("authorized-user is not valid, '/update' will be disabled");
        }
        Logger.Info("version-view plugin start");
    }
    public async override Task OnLoaded()
    {
        data = await Interop.PluginStorage.Load<Data>() ?? new Data();
        Logger.Debug("data loaded");
        bool changed = false;
        //if contains update flag, then reply update info
        if (data.UpdateByGroupId > 0)
        {
            await Actions.SendGroupMessage(data.UpdateByGroupId, $"update successful\n{gitInfo}");
            data.UpdateByGroupId = -1;
            changed = true;
        }
        if (data.ReloadByGroupId > 0)
        {
            await Actions.SendGroupMessage(data.ReloadByGroupId, $"reload successful\n{gitInfo}");
            data.ReloadByGroupId = -1;
            changed = true;
        }
        if (changed)
        {
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
    /// <summary>
    /// 执行git fetch和merge操作，并获取合并前后的提交信息
    /// </summary>
    /// <returns>合并结果和提交信息</returns>
    public static async Task<(string diff, string commitMessages, bool hasChanges)> GitFetchMerge()
    {
        // 先获取当前HEAD的commit哈希值
        string beforeCommit = (await ExecuteGitCommand("rev-parse HEAD")).Trim();

        // 执行fetch和merge
        await ExecuteGitCommand("fetch");
        var diff = await ExecuteGitCommand("merge");

        // 获取合并后的HEAD
        string afterCommit = (await ExecuteGitCommand("rev-parse HEAD")).Trim();

        bool hasChanges = beforeCommit != afterCommit;
        string commitMessages;
        try
        {
            if (hasChanges)
            {
                // 获取两个commit之间的所有提交
                string rangeCommits = await ExecuteGitCommand($"log {beforeCommit.Trim()}..{afterCommit.Trim()} --pretty=format:%s");

                if (string.IsNullOrWhiteSpace(rangeCommits))
                {
                    commitMessages = "没有新的提交信息";
                }
                else
                {
                    // 将多行提交信息格式化为更易读的形式
                    var lines = rangeCommits.Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
            else
            {
                commitMessages = "当前代码已经是最新版本";
            }
        }
        catch (Exception ex)
        {
            commitMessages = $"获取提交信息时出错: {ex.Message}";
        }

        return (diff, commitMessages, hasChanges);
    }
    private async Task Update(long groupId, bool force = false)
    {
        var (diff, commitMessages, hasChanges) = await GitFetchMerge();
        diff = _redundantRegex().Replace(diff, "").Replace("()", "").Trim();

        // No changes — skip update (unless forced)
        if (!hasChanges && !force)
        {
            await Actions.SendGroupMessage(groupId, "当前代码已经是最新版本，无需更新");
            return;
        }

        //store the update info
        data.UpdateByGroupId = groupId;
        await Interop.PluginStorage.Save(data);

        // Determine project root by searching for .git directory or MerryBot.sln
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? projectRoot = FindProjectRoot(baseDir);
        if (projectRoot == null)
        {
            await Actions.SendGroupMessage(groupId, "无法定位项目根目录，更新失败");
            return;
        }
        string buildDir = Path.Combine(projectRoot, "build");
        string activeSlotFile = Path.Combine(buildDir, "active_slot");

        // Read current active slot
        string activeSlot = "A";
        if (File.Exists(activeSlotFile))
        {
            activeSlot = (await File.ReadAllTextAsync(activeSlotFile)).Trim();
        }

        // Target = opposite slot
        string targetSlot = activeSlot == "A" ? "B" : "A";
        string targetDir = Path.Combine(buildDir, $"slot_{targetSlot.ToLower()}");

        await Actions.SendGroupMessage(groupId, $"{diff}\n{commitMessages}\n正在编译到备用槽位 slot_{targetSlot.ToLower()}...");

        // Run build.sh in background
        string buildScript = Path.Combine(projectRoot, "build.sh");
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"\"{buildScript}\" \"{targetDir}\"",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var buildProcess = Process.Start(psi)!;
            string stdout = await buildProcess.StandardOutput.ReadToEndAsync();
            string stderr = await buildProcess.StandardError.ReadToEndAsync();
            await buildProcess.WaitForExitAsync();

            if (buildProcess.ExitCode != 0)
            {
                string errMsg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                Logger.Error($"Build failed: {errMsg}");
                await Actions.SendGroupMessage(groupId, $"编译失败，当前版本继续运行\n{errMsg}");
                return;
            }

            // Build succeeded — atomically update active slot and exit
            string tempFile = activeSlotFile + ".tmp";
            await File.WriteAllTextAsync(tempFile, targetSlot);
            File.Move(tempFile, activeSlotFile, overwrite: true);
            Logger.Info($"Build succeeded, switching to slot {targetSlot}");
            await Actions.SendGroupMessage(groupId, $"编译完成，切换到 slot_{targetSlot.ToLower()}...");
            Interop.Shutdown(CommonLib.ExitCode.PREBUILT);
        }
        catch (Exception ex)
        {
            Logger.Error($"Build process error: {ex.Message}");
            await Actions.SendGroupMessage(groupId, $"编译过程出错: {ex.Message}\n当前版本继续运行");
        }
    }
    private async Task Reload(long groupId)
    {
        //await Actions.SendGroupMessage(groupId, "reloading...\nrestarting...");
        data.ReloadByGroupId = groupId;
        await Interop.PluginStorage.Save(data);
        Interop.Shutdown(CommonLib.ExitCode.RELOAD);
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/version"))
        {
            _ = Actions.SendGroupMessage(groupId, gitInfo);
        }
        else if (IsStartsWith(chain, "/update"))
        {
            if (authorized == data.sender.user_id)
            {
                bool force = chain.ToString().Contains("-f");
                _ = Update(groupId, force: force);
            }
            else
            {
                _ = Actions.SendGroupMessage(groupId, "401 Unauthorized\nPermission Denied");
            }
        }
        else if (IsStartsWith(chain, "/reload"))
        {
            if (authorized == data.sender.user_id)
            {
                _ = Reload(groupId);
            }
            else
            {
                _ = Actions.SendGroupMessage(groupId, "401 Unauthorized\nPermission Denied");
            }
        }
    }
    /// <summary>
    /// Find project root by searching for .git directory or MerryBot.sln, starting from the given directory and walking up.
    /// </summary>
    private static string? FindProjectRoot(string startDir)
    {
        string? dir = startDir;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "MerryBot.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    class Data
    {
        public long UpdateByGroupId = -1;
        public long ReloadByGroupId = -1;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[+\-]")]
    private static partial System.Text.RegularExpressions.Regex _redundantRegex();
}
