using NapcatClient;
using NapcatClient.MessageType;

namespace BotPlugin;

/// <summary>
/// 群命令转发层：/version、/update、/reload 仅做授权校验与消息转发，
/// 实际执行逻辑全部位于宿主 core（<see cref="IHostLifecycle"/>，经 Interop.Lifecycle 访问）。
/// 更新/重载重启后的结果通知由本插件在 OnLoaded 中消费 core 写入的待通知目标并补发。
/// </summary>
[PluginTag("view-version", "版本查看", "/version查看当前版本;/update [-f]更新软件;/reload重启程序")]
public partial class ViewVersion : Plugin
{
    private readonly long authorized;

    public ViewVersion(PluginInterop interop) : base(interop)
    {
        authorized = interop.AuthorizedUser;
        if (authorized < 0)
        {
            Logger.Warn("authorized-user is not valid, '/update' will be disabled");
        }
        Logger.Info("version-view plugin start");
    }

    /// <summary>重启后补发更新/重载结果通知：消费 core 待通知目标并发送到对应群。</summary>
    public override async Task OnLoaded()
    {
        try
        {
            var targets = await Interop.Lifecycle.TakeNotifyTargetsAsync();
            if (targets.UpdateByGroupId <= 0 && targets.ReloadByGroupId <= 0)
            {
                return;
            }
            var gitInfo = await Interop.Lifecycle.GetVersionInfoAsync();
            if (targets.UpdateByGroupId > 0)
            {
                await Channel.SendMessage(GroupSession(targets.UpdateByGroupId), $"update successful\n{gitInfo}");
            }
            if (targets.ReloadByGroupId > 0)
            {
                await Channel.SendMessage(GroupSession(targets.ReloadByGroupId), $"reload successful\n{gitInfo}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"补发更新/重载结果通知失败: {ex.Message}");
        }
    }

    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, MessageContext context)
    {
        if (!isMentioned || command == null) return Task.CompletedTask;
        long groupId = long.Parse(context.Session.Id);
        switch (command.Name)
        {
            case "version":
                _ = SendVersionAsync(groupId);
                break;
            case "update":
                if (authorized == context.SenderId)
                {
                    bool force = command.Args.Contains("-f");
                    _ = Interop.Lifecycle.RequestUpdateAsync(force, groupId);
                }
                else
                {
                    _ = Channel.SendMessage(GroupSession(groupId), "401 Unauthorized\nPermission Denied");
                }
                break;
            case "reload":
                if (authorized == context.SenderId)
                {
                    _ = Interop.Lifecycle.RequestReloadAsync(groupId);
                }
                else
                {
                    _ = Channel.SendMessage(GroupSession(groupId), "401 Unauthorized\nPermission Denied");
                }
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>转发 /version：从 core 获取版本信息并发送到群。</summary>
    private async Task SendVersionAsync(long groupId)
    {
        try
        {
            var info = await Interop.Lifecycle.GetVersionInfoAsync();
            await Channel.SendMessage(GroupSession(groupId), info);
        }
        catch (Exception ex)
        {
            Logger.Warn($"获取版本信息失败: {ex.Message}");
            await Channel.SendMessage(GroupSession(groupId), $"获取版本信息失败: {ex.Message}");
        }
    }

    /// <summary>把 QQ 群号转换为会话键（当前平台固定为 qq 群聊）。</summary>
    private static SessionKey GroupSession(long groupId) => new("qq", "group", groupId.ToString());
}
