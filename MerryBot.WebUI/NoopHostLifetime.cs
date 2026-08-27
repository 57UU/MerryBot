using Microsoft.Extensions.Hosting;

namespace MerryBot.WebUI;

/// <summary>
/// 内嵌到 MerryBot 时使用的 Host lifetime。
/// 进程信号由外层 MerryBot 统一处理，避免 WebUI 的 ConsoleLifetime 与宿主
/// 同时响应 Ctrl+C/SIGTERM 并发起两套关闭流程。
/// </summary>
internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
