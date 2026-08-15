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
}
