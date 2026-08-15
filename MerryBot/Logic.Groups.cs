using DataService;
using MerryBot.WebUI.Api;

namespace MerryBot;

internal partial class Logic : IGroupManager
{
    /// <summary>直接返回 core 配置中的启用群列表；消息过滤（QqGroupIDs）读取同一 List 实例，改动即时生效。</summary>
    public IReadOnlyList<long> GetEnabledGroupIds() => ConfigManager.Instance.QqGroups;

    public Task AddGroupAsync(long groupId)
    {
        var groups = ConfigManager.Instance.QqGroups;
        if (groups.Contains(groupId))
        {
            return Task.CompletedTask;
        }
        groups.Add(groupId);
        return ConfigManager.Save();
    }

    public Task RemoveGroupAsync(long groupId)
    {
        var groups = ConfigManager.Instance.QqGroups;
        return groups.Remove(groupId) ? ConfigManager.Save() : Task.CompletedTask;
    }

    /// <summary>群名缓存缺失时（通常是未启用过的群）实时从 napcat 查询，成功则写回缓存。</summary>
    public async Task<GroupNameInfoDto?> ResolveGroupNameAsync(long groupId)
    {
        // 未连接时直接跳过，避免每群卡住 30 秒超时
        if (!botClient.WebSocketService.WebSocket.IsRunning)
        {
            return null;
        }
        try
        {
            var info = await botClient.Bot.GetGroupInfo(groupId.ToString());
            if (info == null || string.IsNullOrEmpty(info.GroupName))
            {
                return null;
            }
            await historyRecorder.RecordOrUpdateGroupNameAsync(new GroupNameEntry
            {
                GroupId = groupId,
                Name = info.GroupName,
                MemberCount = info.MemberCount,
                MaxMemberCount = info.MaxMemberCount,
                UpdatedTime = DateTime.Now,
            });
            return new GroupNameInfoDto(groupId, info.GroupName, info.MemberCount, info.MaxMemberCount);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
