using NapcatClient;
using NapcatClient.MessageType;
using System.Runtime.InteropServices;

namespace BotPlugin;

[PluginTag("run-command", "Shell", "使用 /sh 运行终端命令")]
public class RunCommand : Plugin
{
    long authorized;
    bool useUnprivileged = true;
    /// <summary>
    /// shell-user 配置，供其他插件使用
    /// </summary>
    public string ShellUser { get; }
    public RunCommand(PluginInterop interop) : base(interop)
    {
        //not Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PluginNotUsableException("shell plugin can only support Linux");
        }
        ShellUser = interop.GetVariableOrSetDefault("shell-user", "merrybot");
        terminal = Terminal.CreateUserTerminal(ShellUser);
        terminal.logger = Logger;
        authorized = interop.AuthorizedUser;
        Logger.Info($"shell plugin started, user: {ShellUser}");
    }
    /// <summary>
    /// 创建一个新的 ShellManager 实例（使用当前配置的 shell-user）
    /// </summary>
    public ShellManager CreateShellManager() => new(ShellUser);
    public override void OnGroupMessage(bool isMentioned, Command? command, ReceivedGroupMessage data)
    {
        if (!isMentioned || command?.Name != "sh") return;
        long groupId = data.GroupId;
        long sender = data.sender.user_id;
        var isAuthorized = sender == authorized;
        if (useUnprivileged == false && !isAuthorized)
        {
            _ = Bot.SendGroupMessage(groupId, "401 Unauthorized\nYou do not have the permission");
            return;
        }
        if (command.Args.Length == 0)
        {
            _ = Bot.SendGroupMessage(groupId, "请输入命令");
            return;
        }
        string text = string.Join(' ', command.Args);
        _ = HandleCommand(text, groupId, sender, isAuthorized);
    }
    internal Terminal terminal;
    async Task HandleCommand(string command, long groupId, long sender, bool isAuthorized = false)
    {
        string result;
        try
        {
            result = await terminal.RunCommandAsync(command, timeoutMs: 3000, useHardTimeout: true);
        }
        catch (Exception e)
        {
            result = $"error:{e.Message}";
        }

        result = PluginUtils.ConstraintLength(result, 3000);

        await Bot.ChooseBestReplyMethod(groupId, sender.ToString(), result);
    }

}


