using NapcatClient;
using System.Text;

namespace BotPlugin;

[PluginTag("help", "帮助", "使用 /help 来查看帮助")]
public class Help : Plugin
{
    IEnumerable<PluginInfo>? pluginTags;
    public Help(PluginInterop interop) : base(interop)
    {
    }
    public async override Task OnLoaded()
    {
        pluginTags = Interop.PluginInfoGetter();
    }
    public override void OnGroupMessage(bool isMentioned, Command? command, ReceivedGroupMessage data)
    {
        if (!isMentioned || command?.Name != "help")
        {
            return;
        }
        if (pluginTags == null)
        {
            _ = Bot.SendGroupMessage(data.GroupId, "尚未完成加载");
            return;
        }
        var groupId = data.GroupId;
        var sb = new StringBuilder();
        int count = 1;

        foreach (var i in pluginTags)
        {
            if (i.PluginTag.Type == PluginType.Interactive)
            {
                if (!i.Instance.IsEnable)
                {
                    sb.Append("[已停用]");
                }
                sb.AppendLine($"{count++}. {i.PluginTag.Name} : {i.PluginTag.Description}");
            }
        }
        //display admin plugins for admin user
        sb.AppendLine("- 管理员插件：");
        if (data.self_id == Interop.AuthorizedUser)
        {
            foreach (var i in pluginTags)
            {
                if (i.PluginTag.Type == PluginType.Admin)
                {
                    if (!i.Instance.IsEnable)
                    {
                        sb.Append("[已停用]");
                    }
                    sb.AppendLine($"{count++}. {i.PluginTag.Name} : {i.PluginTag.Description}");
                }
            }
        }
        var help = $"欢迎使用MerryBot\n已加载如下插件：\n{sb.ToString().TrimEnd('\n')}";
        _ = Bot.SendGroupMessage(groupId, help);
    }
}
