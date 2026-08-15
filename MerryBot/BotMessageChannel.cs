using BotPlugin;
using CommonLib;
using NapcatClient.Action;
using NapcatClient.MessageType;

namespace MerryBot;

/// <summary>
/// <see cref="MessageChannel"/> 的宿主实现：发送失败仅记录日志（含插件 id 与会话），不向上抛出异常。
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
    public async Task SendMessage(SessionKey session, string message)
    {
        try { await _bot.SendGroupMessage(long.Parse(session.Id), message); }
        catch (Exception ex) { _logger.Error($"插件 {_pluginId} 发送消息失败, session={session}: {ex.Message}"); }
    }
    public async Task SendMessage(SessionKey session, IEnumerable<TypedMessage> messageChain)
    {
        try { await _bot.SendGroupMessage(long.Parse(session.Id), messageChain); }
        catch (Exception ex) { _logger.Error($"插件 {_pluginId} 发送消息失败, session={session}: {ex.Message}"); }
    }
}
