using MerryBot.Contracts;

namespace BotPlugin;

[ConfigDescription("自动+1配置", "")]
public class AutoIncreaseConfig : IPluginConfig
{
    [ConfigDescription("重复次数", "重复次数，默认3次")]
    public int RepeatTime { get; set; } = 3;
}