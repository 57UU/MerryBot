using MerryBot.Contracts;

namespace BotPlugin;

/// <summary>Agent 会话控制（供 WebUI 会话快照页）：查询忙闲、清除指定会话。</summary>
public partial class AgentPlugin : IAgentSessionControlService
{
    public Task<bool> IsSessionBusyAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGroupSessionKey(sessionKey);
        return Task.FromResult(IsSessionBusy(sessionKey));
    }

    public async Task<bool> TryClearSessionAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGroupSessionKey(sessionKey);
        // 先查忙：正处理消息时拒绝清除，避免与生成中的对话竞态清空上下文
        if (IsSessionBusy(sessionKey))
        {
            return false;
        }
        if (sessionManager.TryGetLiveSession(sessionKey, out var session) && session is not null)
        {
            // 二次确认：查询与清除之间可能有新消息入队
            if (session.IsBusy)
            {
                return false;
            }
            // 与群聊 /new 同语义：先清空内存与持久化历史，再重建会话以刷新工具集
            await session.ResetAsync();
            await sessionManager.RebuildSessionAsync(sessionKey);
        }
        else
        {
            // 无常驻会话（如已被空闲淘汰）：直接删除持久化快照
            await new DatabaseContextHistory(Interop.PluginStorage.PluginDatabaseScope, sessionKey).Clear();
        }
        Logger.Info($"已通过 WebUI 清除会话 {sessionKey}");
        return true;
    }

    /// <summary>插件层排队或调度中也算忙：调度循环尚未把消息投给 AgentSession 之前，会话 IsBusy 仍为 false。</summary>
    private bool IsSessionBusy(string sessionKey)
    {
        if (pendingMessages.TryGetValue(sessionKey, out var pending))
        {
            lock (pending.SyncRoot)
            {
                if (pending.IsDispatching || pending.Items.Count > 0)
                {
                    return true;
                }
            }
        }
        return sessionManager.IsSessionBusy(sessionKey);
    }

    /// <summary>
    /// 会话 key 须为 QQ 群会话（qq/group/群号）：非法 key 直接抛 400，
    /// 避免 Rebuild 路径误为未知 key 创建会话。
    /// </summary>
    private static void EnsureGroupSessionKey(string sessionKey)
    {
        var parsed = SessionKey.Parse(sessionKey);
        if (parsed.Platform != "qq" || parsed.ChannelType != "group")
        {
            throw new ArgumentException("仅支持 QQ 群会话。", nameof(sessionKey));
        }
    }
}
