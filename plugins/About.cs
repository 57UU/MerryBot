using NapcatClient;

namespace BotPlugin;

[PluginTag("About", "使用 /about 来查看关于", isIgnore: true)]
public class About : Plugin
{
    private const string aboutMessage =
"""
# -------About-------

Merry Bot

本程序的目的是实现QQ机器人的模块化开发，以插件的形式增加功能

访问Github仓库 https://github.com/57UU/MerryBot 以获取更多信息
""";

    public About(PluginInterop interop) : base(interop)
    {
        Logger.Info("about plugin start");
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/about"))
        {
            _ = Actions.SendGroupMessage(groupId, aboutMessage);
        }
    }
}
