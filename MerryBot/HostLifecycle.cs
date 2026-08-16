using CommonLib;
using DataProvider;
using System.Diagnostics;
using System.Text;

namespace MerryBot;

/// <summary>
/// core 拥有的进程生命周期实现：版本查看、检测更新、完整更新（git fetch+merge →
/// 编译备用槽 → 切槽 → 重启）、重启、重载与退出。
/// 原 ViewVersion 插件的 git/构建/切槽逻辑整体迁移至此，插件与 WebUI 仅通过
/// <see cref="IHostLifecycle"/> 请求操作；更新/重载后的结果通知由 core 在重启后补发。
/// </summary>
internal sealed partial class HostLifecycle : IHostLifecycle
{
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Action<int> shutdown;
    private readonly PluginStorageDatabase database;
    /// <summary>更新互斥锁：同一时间只允许一个更新流程，重入直接提示</summary>
    private static readonly SemaphoreSlim updateLock = new(1, 1);
    /// <summary>git 命令超时时间</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    /// <summary>编译超时时间</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);
    /// <summary>core 生命周期通知标志的存储键（Plugin_Data_Table）</summary>
    private const string NotifyKey = "core-lifecycle";

    public HostLifecycle(
        Action<int> shutdown,
        PluginStorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(database);
        this.shutdown = shutdown;
        this.database = database;
    }

    public Task<string> GetVersionInfoAsync(CancellationToken cancellationToken = default)
        => GetGitInfoAsync(cancellationToken);

    public async Task<UpdateCheckResult> CheckUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var versionInfo = await GetGitInfoAsync(cancellationToken);
            await ExecuteGitCommand("fetch", cancellationToken);
            var head = (await ExecuteGitCommand("rev-parse HEAD", cancellationToken)).Trim();
            // @{u} = 当前分支的 upstream；未配置 upstream 时按无更新处理并给出提示
            string? remoteHead;
            try
            {
                remoteHead = (await ExecuteGitCommand("rev-parse @{u}", cancellationToken)).Trim();
            }
            catch (InvalidOperationException)
            {
                return new UpdateCheckResult(versionInfo, false, null);
            }

            var hasUpdate = !string.Equals(head, remoteHead, StringComparison.Ordinal);
            if (!hasUpdate)
            {
                return new UpdateCheckResult(versionInfo, false, null);
            }

            var log = await ExecuteGitCommand("log HEAD..@{u} --pretty=format:%s", cancellationToken);
            return new UpdateCheckResult(versionInfo, true, FormatCommitMessages(log));
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult($"检测更新失败: {ex.Message}", false, null);
        }
    }

    public async Task RequestUpdateAsync(
        bool force,
        Func<string, Task>? notifier = null,
        string? notifyTarget = null,
        CancellationToken cancellationToken = default)
    {
        if (!updateLock.Wait(0))
        {
            await NotifyAsync(notifier, "正在更新中，请稍候");
            return;
        }
        try
        {
            var (diff, commitMessages, hasChanges) = await GitFetchMergeAsync(cancellationToken);
            diff = _redundantRegex().Replace(diff, "").Replace("()", "").Trim();

            if (!hasChanges && !force)
            {
                await NotifyAsync(notifier, "当前代码已经是最新版本，无需更新");
                return;
            }

            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string? projectRoot = FindProjectRoot(baseDir);
            if (projectRoot == null)
            {
                await NotifyAsync(notifier, "无法定位项目根目录，更新失败");
                return;
            }
            string buildDir = Path.Combine(projectRoot, "build");
            string activeSlotFile = Path.Combine(buildDir, "active_slot");

            string activeSlot = "A";
            if (File.Exists(activeSlotFile))
            {
                activeSlot = (await File.ReadAllTextAsync(activeSlotFile, cancellationToken)).Trim();
            }
            string targetSlot = activeSlot == "A" ? "B" : "A";
            string targetDir = Path.Combine(buildDir, $"slot_{targetSlot.ToLower()}");

            await NotifyAsync(notifier, $"{diff}\n{commitMessages}\n正在编译到备用槽位 slot_{targetSlot.ToLower()}...");

            // 编译到备用槽：build.sh <target_dir>，20 分钟超时
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
            using var buildTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            buildTimeoutCts.CancelAfter(BuildTimeout);
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
                logger.Error($"Build failed: {errMsg}");
                await NotifyAsync(notifier, $"编译失败，当前版本继续运行\n{errMsg}");
                return;
            }

            // 构建成功：原子切换 active_slot 后重启（launch.sh PREBUILT 分支切槽）
            string tempFile = activeSlotFile + ".tmp";
            await File.WriteAllTextAsync(tempFile, targetSlot, cancellationToken);
            File.Move(tempFile, activeSlotFile, overwrite: true);
            logger.Info($"Build succeeded, switching to slot {targetSlot}");
            await NotifyAsync(notifier, $"编译完成，切换到 slot_{targetSlot.ToLower()}...");
            if (notifyTarget is { } target)
            {
                await SetNotifyFlagAsync(updateTarget: target);
            }
            shutdown(CommonLib.ExitCode.PREBUILT);
        }
        catch (Exception ex)
        {
            logger.Error($"Update process error: {ex.Message}");
            await NotifyAsync(notifier, $"更新过程出错: {ex.Message}\n当前版本继续运行");
        }
        finally
        {
            updateLock.Release();
        }
    }

    public Task RequestRestartAsync()
    {
        shutdown(CommonLib.ExitCode.RESTART);
        return Task.CompletedTask;
    }

    public async Task RequestReloadAsync(string? notifyTarget = null)
    {
        if (notifyTarget is { } target)
        {
            await SetNotifyFlagAsync(reloadTarget: target);
        }
        shutdown(CommonLib.ExitCode.RELOAD);
    }

    public Task RequestExitAsync()
    {
        shutdown(0);
        return Task.CompletedTask;
    }

    /// <summary>消费重启后待补发的通知目标：取走后立即清除，保证只通知一次。</summary>
    public async Task<LifecycleNotifyTargets> TakeNotifyTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flag = await GetNotifyAsync();
        await database.StorePluginData(NotifyKey, new CoreLifecycleNotify());
        return new LifecycleNotifyTargets(flag.UpdateNotifyTarget, flag.ReloadNotifyTarget);
    }

    /// <summary>更新/重载完成后补发的通知标志（重启后由插件消费并发送结果）。</summary>
    private sealed class CoreLifecycleNotify
    {
        public string? UpdateNotifyTarget { get; set; }
        public string? ReloadNotifyTarget { get; set; }
    }

    private async Task<CoreLifecycleNotify> GetNotifyAsync()
    {
        var data = await database.GetPluginData(NotifyKey);
        return data as CoreLifecycleNotify ?? new CoreLifecycleNotify();
    }

    private async Task SetNotifyFlagAsync(string? updateTarget = null, string? reloadTarget = null)
    {
        var flag = await GetNotifyAsync();
        if (updateTarget is { } ut) flag.UpdateNotifyTarget = ut;
        if (reloadTarget is { } rt) flag.ReloadNotifyTarget = rt;
        await database.StorePluginData(NotifyKey, flag);
    }

    /// <summary>把进度消息交给调用方提供的 notifier；发送失败只记日志，不中断更新流程。</summary>
    private static async Task NotifyAsync(Func<string, Task>? notifier, string message)
    {
        if (notifier == null)
        {
            return;
        }
        try
        {
            await notifier(message);
        }
        catch (Exception ex)
        {
            logger.Warn($"发送更新通知失败: {ex.Message}");
        }
    }

    /// <summary>执行 Git 命令并返回输出；退出码非 0 或超时抛 <see cref="InvalidOperationException"/>。</summary>
    private static async Task<string> ExecuteGitCommand(string arguments, CancellationToken cancellationToken = default)
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitTimeout);
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

    private static async Task<string> GetGitInfoAsync(CancellationToken cancellationToken = default)
    {
        // 使用单个命令获取大部分信息
        string gitLogFormat = "--pretty=format:%H|%ci|%s";
        string logOutput = await ExecuteGitCommand($"log -1 {gitLogFormat}", cancellationToken);

        string[] logParts = logOutput.Split('|');
        if (logParts.Length < 3)
        {
            return "解析Git日志信息失败";
        }

        string commitHash = logParts[0];
        string commitDate = logParts[1];
        string commitMessage = logParts[2];

        string commitCount = await ExecuteGitCommand("rev-list --count HEAD", cancellationToken);
        string userName = await ExecuteGitCommand("config user.name", cancellationToken);

        StringBuilder gitInfo = new();
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

    /// <summary>执行 git fetch 和 merge，返回合并 diff、提交信息与是否有变更。</summary>
    private static async Task<(string diff, string commitMessages, bool hasChanges)> GitFetchMergeAsync(CancellationToken cancellationToken = default)
    {
        string beforeCommit = (await ExecuteGitCommand("rev-parse HEAD", cancellationToken)).Trim();

        await ExecuteGitCommand("fetch", cancellationToken);
        var diff = await ExecuteGitCommand("merge", cancellationToken);

        string afterCommit = (await ExecuteGitCommand("rev-parse HEAD", cancellationToken)).Trim();

        bool hasChanges = beforeCommit != afterCommit;
        string commitMessages;
        try
        {
            if (hasChanges)
            {
                string rangeCommits = await ExecuteGitCommand(
                    $"log {beforeCommit.Trim()}..{afterCommit.Trim()} --pretty=format:%s", cancellationToken);
                commitMessages = FormatCommitMessages(rangeCommits);
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

    /// <summary>把多行提交信息格式化为更易读的形式。</summary>
    private static string FormatCommitMessages(string rangeCommits)
    {
        if (string.IsNullOrWhiteSpace(rangeCommits))
        {
            return "没有新的提交信息";
        }
        var lines = rangeCommits.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 1)
        {
            return lines[0];
        }
        var sb = new StringBuilder();
        sb.AppendLine($"合并了 {lines.Length} 个提交:");
        for (int i = 0; i < lines.Length; i++)
        {
            sb.AppendLine($"{i + 1}. {lines[i]}");
        }
        return sb.ToString();
    }

    /// <summary>从给定目录向上查找项目根目录（含 .git 或 MerryBot.sln）。</summary>
    private static string? FindProjectRoot(string startDir)
    {
        string? dir = startDir;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "MerryBot.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // 只移除统计条（如 "| 5 +++--"）中的连续 +/- 符号簇，
    // 避免把路径名中的连字符或 "insertions(+)" 等单符号误删
    [System.Text.RegularExpressions.GeneratedRegex(@"[+\-]{2,}")]
    private static partial System.Text.RegularExpressions.Regex _redundantRegex();
}
