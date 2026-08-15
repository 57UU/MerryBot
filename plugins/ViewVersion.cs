using NapcatClient;
using System.Diagnostics;
using System.Text;

namespace BotPlugin;

[PluginTag("view-version", "版本查看", "/version查看当前版本;/update [-f]更新软件;/reload重启程序")]
public partial class ViewVersion : Plugin
{
    private string gitInfo;
    private long authorized;
#pragma warning disable CS8625
    //data will be loaded in `OnLoaded` function
    private Data data = null;
#pragma warning restore CS8625
    /// <summary>/update 互斥锁：同一时间只允许一个更新流程，重入直接提示</summary>
    private static readonly SemaphoreSlim updateLock = new(1, 1);
    /// <summary>git 命令超时时间</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    /// <summary>编译超时时间</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);

    public ViewVersion(PluginInterop interop) : base(interop)
    {
        try
        {
            gitInfo = GetGitInfo().GetAwaiter().GetResult().Trim();
        }
        catch (PluginNotUsableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            //git 缺失或异常时统一按 PluginNotUsableException 处理，由 PluginInitializer 跳过本插件
            throw new PluginNotUsableException($"获取Git信息失败: {ex.Message}");
        }
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
            await Channel.SendMessage(GroupSession(data.UpdateByGroupId), $"update successful\n{gitInfo}");
            data.UpdateByGroupId = -1;
            changed = true;
        }
        if (data.ReloadByGroupId > 0)
        {
            await Channel.SendMessage(GroupSession(data.ReloadByGroupId), $"reload successful\n{gitInfo}");
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
    /// <exception cref="InvalidOperationException">命令退出码非 0 或执行超时</exception>
    private static async Task<string> ExecuteGitCommand(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        using var timeoutCts = new CancellationTokenSource(GitTimeout);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process already exited */ }
            try { await Task.WhenAll(outputTask, errorTask); } catch { /* output tasks cancelled */ }
            throw new InvalidOperationException($"git {arguments} 执行超时（{GitTimeout.TotalSeconds}秒）");
        }
        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            string errDetail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"git {arguments} 执行失败（ExitCode={process.ExitCode}）: {errDetail}");
        }
        return output;
    }

    public static async Task<string> GetGitInfo()
    {
        try
        {
            // 使用单个命令获取大部分信息
            string gitLogFormat = "--pretty=format:%H|%ci|%s";
            string logOutput = await ExecuteGitCommand($"log -1 {gitLogFormat}");

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
            // 提交哈希可能不足 12 位，取 12 与长度的较小值
            string shortHash = commitHash.Length >= 12 ? commitHash[..12] : commitHash;
            gitInfo.AppendLine($"Commit: {shortHash}");
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
        if (!updateLock.Wait(0))
        {
            await Channel.SendMessage(GroupSession(groupId), "正在更新中，请稍候");
            return;
        }
        try
        {
            var (diff, commitMessages, hasChanges) = await GitFetchMerge();
            diff = _redundantRegex().Replace(diff, "").Replace("()", "").Trim();

            // No changes — skip update (unless forced)
            if (!hasChanges && !force)
            {
                await Channel.SendMessage(GroupSession(groupId), "当前代码已经是最新版本，无需更新");
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
                await Channel.SendMessage(GroupSession(groupId), "无法定位项目根目录，更新失败");
                await ResetUpdateFlagAsync();
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

            await Channel.SendMessage(GroupSession(groupId), $"{diff}\n{commitMessages}\n正在编译到备用槽位 slot_{targetSlot.ToLower()}...");

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

            using var buildProcess = Process.Start(psi)!;
            using var buildTimeoutCts = new CancellationTokenSource(BuildTimeout);
            var stdoutTask = buildProcess.StandardOutput.ReadToEndAsync(buildTimeoutCts.Token);
            var stderrTask = buildProcess.StandardError.ReadToEndAsync(buildTimeoutCts.Token);
            try
            {
                await buildProcess.WaitForExitAsync(buildTimeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { buildProcess.Kill(entireProcessTree: true); } catch { /* process already exited */ }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* output tasks cancelled */ }
                throw new InvalidOperationException($"编译超时（{BuildTimeout.TotalMinutes}分钟）");
            }
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (buildProcess.ExitCode != 0)
            {
                string errMsg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                Logger.Error($"Build failed: {errMsg}");
                await Channel.SendMessage(GroupSession(groupId), $"编译失败，当前版本继续运行\n{errMsg}");
                await ResetUpdateFlagAsync();
                return;
            }

            // Build succeeded — atomically update active slot and exit
            string tempFile = activeSlotFile + ".tmp";
            await File.WriteAllTextAsync(tempFile, targetSlot);
            File.Move(tempFile, activeSlotFile, overwrite: true);
            Logger.Info($"Build succeeded, switching to slot {targetSlot}");
            await Channel.SendMessage(GroupSession(groupId), $"编译完成，切换到 slot_{targetSlot.ToLower()}...");
            // 稳妥起见：成功路径也先复位标志再关闭进程
            await ResetUpdateFlagAsync();
            Interop.Shutdown(CommonLib.ExitCode.PREBUILT);
        }
        catch (Exception ex)
        {
            Logger.Error($"Update process error: {ex.Message}");
            await Channel.SendMessage(GroupSession(groupId), $"更新过程出错: {ex.Message}\n当前版本继续运行");
            await ResetUpdateFlagAsync();
        }
        finally
        {
            updateLock.Release();
        }
    }
    /// <summary>
    /// 清除 update 标志并保存，避免更新失败后重启误报 "update successful"。
    /// 自身异常只记日志，不向上抛出。
    /// </summary>
    private async Task ResetUpdateFlagAsync()
    {
        try
        {
            if (data.UpdateByGroupId > 0)
            {
                data.UpdateByGroupId = -1;
                await Interop.PluginStorage.Save(data);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"reset update flag failed: {ex.Message}");
        }
    }
    private async Task Reload(long groupId)
    {
        //await Actions.SendGroupMessage(groupId, "reloading...\nrestarting...");
        data.ReloadByGroupId = groupId;
        await Interop.PluginStorage.Save(data);
        Interop.Shutdown(CommonLib.ExitCode.RELOAD);
    }
    /// <summary>
    /// 包装 Update：捕获全部异常并向群内反馈，避免 fire-and-forget 调用时用户无反馈
    /// </summary>
    private async Task HandleUpdateAsync(long groupId, bool force)
    {
        try
        {
            await Update(groupId, force: force);
        }
        catch (Exception ex)
        {
            Logger.Error($"update failed: {ex.Message}");
            await Channel.SendMessage(GroupSession(groupId), $"更新失败: {ex.Message}");
        }
    }
    /// <summary>
    /// 包装 Reload：捕获全部异常并向群内反馈；失败时复位标志避免误报
    /// </summary>
    private async Task HandleReloadAsync(long groupId)
    {
        try
        {
            await Reload(groupId);
        }
        catch (Exception ex)
        {
            Logger.Error($"reload failed: {ex.Message}");
            await ResetReloadFlagAsync();
            await Channel.SendMessage(GroupSession(groupId), $"重启失败: {ex.Message}");
        }
    }
    private async Task ResetReloadFlagAsync()
    {
        try
        {
            if (data.ReloadByGroupId > 0)
            {
                data.ReloadByGroupId = -1;
                await Interop.PluginStorage.Save(data);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"reset reload flag failed: {ex.Message}");
        }
    }
    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<NapcatClient.MessageType.TypedMessage> messageChain, MessageContext context)
    {
        if (!isMentioned || command == null) return Task.CompletedTask;
        long groupId = long.Parse(context.Session.Id);
        if (command.Name == "version")
        {
            _ = Channel.SendMessage(GroupSession(groupId), gitInfo);
        }
        else if (command.Name == "update")
        {
            if (authorized == context.SenderId)
            {
                bool force = command.Args.Contains("-f");
                _ = HandleUpdateAsync(groupId, force);
            }
            else
            {
                _ = Channel.SendMessage(GroupSession(groupId), "401 Unauthorized\nPermission Denied");
            }
        }
        else if (command.Name == "reload")
        {
            if (authorized == context.SenderId)
            {
                _ = HandleReloadAsync(groupId);
            }
            else
            {
                _ = Channel.SendMessage(GroupSession(groupId), "401 Unauthorized\nPermission Denied");
            }
        }
        return Task.CompletedTask;
    }
    /// <summary>把 QQ 群号转换为会话键（当前平台固定为 qq 群聊）。</summary>
    private static SessionKey GroupSession(long groupId) => new("qq", "group", groupId.ToString());
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

    // 只移除统计条（如 "| 5 +++--"）中的连续 +/- 符号簇，
    // 避免把路径名中的连字符或 "insertions(+)" 等单符号误删
    [System.Text.RegularExpressions.GeneratedRegex(@"[+\-]{2,}")]
    private static partial System.Text.RegularExpressions.Regex _redundantRegex();
}
