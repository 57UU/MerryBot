using System.Diagnostics;
using Agent.Session;

namespace MerryBot.Test;

/// <summary>
/// Terminal 前台超时自动转后台（backgroundOnTimeout）行为测试。
/// 依赖环境中存在 bash（Git Bash / Linux 均可）。
/// </summary>
public class TerminalBackgroundTimeoutTests
{
    [Fact]
    public async Task 超时带flag_转为后台并正常完成()
    {
        using var terminal = Terminal.Create("bash");
        var sw = Stopwatch.StartNew();
        var result = await terminal.RunCommandAsync("sleep 4; echo done", null, 1, backgroundOnTimeout: true);

        // 调用应在超时周期（1 秒量级）返回，而不是等命令跑完
        Assert.True(sw.Elapsed.TotalSeconds < 3, $"应快速返回，实际 {sw.Elapsed.TotalSeconds:F1} 秒");
        Assert.NotNull(result.Detached);
        Assert.Contains("转入后台", result.Output);

        // 后台续跑完成后拿到全量输出（含前台已收到的部分）
        var output = await result.Detached!.Completion;
        Assert.Contains("done", output);

        // 转后台后共享终端已重启，前台可继续使用
        var after = await terminal.RunCommandAsync("echo ok", null, 10);
        Assert.Contains("ok", after.Output);
    }

    [Fact]
    public async Task 超时不带flag_保持终止重启行为()
    {
        using var terminal = Terminal.Create("bash");
        var result = await terminal.RunCommandAsync("sleep 3; echo done", null, 1);

        Assert.Null(result.Detached);
        Assert.Contains("已终止并重启 shell", result.Output);
    }
}
