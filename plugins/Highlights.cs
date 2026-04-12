using System;
using System.Collections.Generic;
using System.Text;

namespace BotPlugin;

[PluginTag("highlights", "Highlights", "群刊插件", priority: 1001, type: PluginType.Interactive,isIgnore:true)]
public class Highlights : Plugin
{
    public Highlights(PluginInterop interop) : base(interop)
    {
    }

}
