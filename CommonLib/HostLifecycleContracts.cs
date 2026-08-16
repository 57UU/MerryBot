namespace CommonLib;

/// <summary>更新检测结果：当前版本信息 + 是否存在可用更新及新提交列表。</summary>
public sealed record UpdateCheckResult(
    string VersionInfo,
    bool HasUpdate,
    string? CommitMessages);

/// <summary>重启后待补发的通知目标（core 在更新/重载流程成功时写入，消费后清除）。-1 表示无待通知。</summary>
public sealed record LifecycleNotifyTargets(
    long UpdateByGroupId,
    long ReloadByGroupId);

/// <summary>
/// core 拥有的进程生命周期能力（版本查看 / 检测更新 / 更新 / 重启 / 重载 / 退出）。
/// 插件与 WebUI 通过它请求系统级操作，实际执行逻辑全部位于宿主。
/// </summary>
public interface IHostLifecycle
{
    /// <summary>获取当前版本 git 信息（/version 显示用）。</summary>
    Task<string> GetVersionInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>检测是否有可用更新（git fetch + 对比 HEAD 与远端，不执行 merge）。</summary>
    Task<UpdateCheckResult> CheckUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求执行完整更新：fetch+merge → 编译到备用槽 → 切槽 → 重启（PREBUILT）。
    /// <paramref name="notifyGroupId"/> 非空时，更新过程中的反馈与重启后的结果通知发送到该群。
    /// </summary>
    Task RequestUpdateAsync(bool force, long? notifyGroupId = null, CancellationToken cancellationToken = default);

    /// <summary>请求重启（重新编译当前槽）。</summary>
    Task RequestRestartAsync();

    /// <summary>请求重载（不编译直接重启）。<paramref name="notifyGroupId"/> 非空时重启后发送结果通知。</summary>
    Task RequestReloadAsync(long? notifyGroupId = null);

    /// <summary>
    /// 消费重启后待补发的通知目标：core 在更新/重载流程成功时写入待通知群号，
    /// 由插件在 OnLoaded 中调用本方法取走并发消息；取走后即清除，保证只通知一次。
    /// </summary>
    Task<LifecycleNotifyTargets> TakeNotifyTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>请求退出进程。</summary>
    Task RequestExitAsync();
}
