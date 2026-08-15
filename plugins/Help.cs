using NapcatClient;
using NapcatClient.MessageType;
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
    public override Task OnMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, MessageContext context)
    {
        if (!isMentioned || command?.Name != "help")
        {
            return Task.CompletedTask;
        }
        if (pluginTags == null)
        {
            _ = Channel.SendMessage(context.Session, "尚未完成加载");
            return Task.CompletedTask;
        }
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
        if (context.SenderId == Interop.AuthorizedUser)
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
        _ = Channel.SendMessage(context.Session, help);
        return Task.CompletedTask;
    }
}
