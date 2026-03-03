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
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (pluginTags == null)
        {
            _ = Actions.SendGroupMessage(groupId, "尚未完成加载");
            return;
        }
        if (!IsStartsWith(chain, "/help"))
        {
            return;
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
                sb.AppendLine($"{count++}. {i.PluginTag.Id} : {i.PluginTag.Description}");
            }
        }
        var help = $"欢迎使用MerryBot\n已加载如下插件：\n{sb.ToString().TrimEnd('\n')}";
        _ = Actions.SendGroupMessage(groupId, help);
    }
}
