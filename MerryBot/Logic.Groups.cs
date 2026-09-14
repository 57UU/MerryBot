using DataService;
using MerryBot.WebUI.Api;
using NapcatClient;

namespace MerryBot;

internal partial class Logic : IGroupManager
{
    /// <summary>返回启用群列表的线程安全快照；消息过滤读取同一数据源，改动即时生效。</summary>
    public IReadOnlyList<long> GetEnabledGroupIds() => ConfigManager.GetGroupIdsSnapshot();

    /// <summary>群组变更走 ConfigManager 的锁与序列化路径（与 WebUI 配置保存共用），避免并发竞争。</summary>
    public Task AddGroupAsync(long groupId) => ConfigManager.AddGroupAsync(groupId);

    public Task RemoveGroupAsync(long groupId) => ConfigManager.RemoveGroupAsync(groupId);

    /// <summary>群名缓存缺失时（通常是未启用过的群）实时从 napcat 查询，成功则写回缓存。</summary>
    public async Task<GroupNameInfoDto?> ResolveGroupNameAsync(long groupId)
    {
        // 未连接时直接跳过，避免每群卡住 30 秒超时
        if (botClient.State != AdapterState.Connected)
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

    /// <summary>真批量：一次 get_group_list 拿全量群后过滤目标 id 并写缓存；失败/超时返回空字典，由调用方回退单查。</summary>
    public async Task<Dictionary<long, GroupNameInfoDto>> ResolveGroupNamesAsync(IReadOnlyList<long> groupIds)
    {
        Dictionary<long, GroupNameInfoDto> result = new();
        if (groupIds.Count == 0 || botClient.State != AdapterState.Connected)
        {
            return result;
        }
        try
        {
            Task<List<GroupInfo>> lookup = botClient.Bot.GetGroupListAsync();
            Task completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(3)));
            if (completed != lookup)
            {
                return result;
            }
            HashSet<long> wanted = groupIds.ToHashSet();
            foreach (GroupInfo info in await lookup)
            {
                if (info == null || !wanted.Contains(info.GroupId) || string.IsNullOrEmpty(info.GroupName))
                {
                    continue;
                }
                await historyRecorder.RecordOrUpdateGroupNameAsync(new GroupNameEntry
                {
                    GroupId = info.GroupId,
                    Name = info.GroupName,
                    MemberCount = info.MemberCount,
                    MaxMemberCount = info.MaxMemberCount,
                    UpdatedTime = DateTime.Now,
                });
                result[info.GroupId] = new GroupNameInfoDto(info.GroupId, info.GroupName, info.MemberCount, info.MaxMemberCount);
            }
        }
        catch (Exception)
        {
            return result;
        }
        return result;
    }
}
