using BotPlugin;
using CommonLib;
using NapcatClient.Action;
using NapcatClient.MessageType;

namespace MerryBot;

/// <summary>
/// <see cref="MessageChannel"/> 的宿主实现：发送失败仅记录日志（含插件 id），不向上抛出异常。
/// </summary>
public sealed class BotMessageChannel : MessageChannel
{
    private readonly Actions _bot;
    private readonly ISimpleLogger _logger;
    private readonly string _pluginId;
    public BotMessageChannel(Actions bot, ISimpleLogger logger, string pluginId)
    {
        _bot = bot;
        _logger = logger;
        _pluginId = pluginId;
    }
    public async Task SendGroupMessage(long groupId, string message)
    {
        try { await _bot.SendGroupMessage(groupId, message); }
        catch (Exception ex) { _logger.Error($"插件 {_pluginId} 发送群消息失败, group={groupId}: {ex.Message}"); }
    }
    public async Task SendGroupMessage(long groupId, IEnumerable<TypedMessage> messageChain)
    {
        try { await _bot.SendGroupMessage(groupId, messageChain); }
        catch (Exception ex) { _logger.Error($"插件 {_pluginId} 发送群消息失败, group={groupId}: {ex.Message}"); }
    }
}
