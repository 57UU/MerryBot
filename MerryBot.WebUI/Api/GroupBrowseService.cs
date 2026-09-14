using BotPlugin;
using CommonLib;
using DataService;

namespace MerryBot.WebUI.Api;

/// <summary>群聊启用的读写入口；实现由主程序提供（直接操作 core 配置）。</summary>
public interface IGroupManager
{
    IReadOnlyList<long> GetEnabledGroupIds();
    Task AddGroupAsync(long groupId);
    Task RemoveGroupAsync(long groupId);

    /// <summary>实时从 napcat 查询群名/人数并写入缓存；未连接或查询失败时返回 null。</summary>
    Task<GroupNameInfoDto?> ResolveGroupNameAsync(long groupId);
}

public sealed record GroupNameInfoDto(
    long GroupId,
    string Name,
    int MemberCount,
    int MaxMemberCount);

public sealed record GroupEntryDto(
    long GroupId,
    string? Name,
    int MemberCount,
    int MessageCount,
    int AiMessageCount,
    bool Enabled);

public sealed record GroupListDto(
    IReadOnlyList<GroupEntryDto> Groups);

/// <summary>群组浏览直连服务：列表拼装逻辑与 GroupApiMapper 的 GET 分支同体，供群组管理页直接调用。</summary>
public static class GroupBrowseService
{
    public static async Task<GroupListDto> GetGroupsAsync(IGroupManager manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        List<long> knownGroupIds = await historyRecorder.GetAllGroupIdsAsync();
        List<long> enabledIds = manager.GetEnabledGroupIds().OrderBy(x => x).ToList();
        HashSet<long> enabledSet = enabledIds.ToHashSet();
        List<GroupNameEntry> names = await historyRecorder.GetAllGroupNamesAsync();
        Dictionary<long, GroupNameEntry> nameMap = names.ToDictionary(x => x.GroupId);

        // 已启用的群即使还没有历史记录也一并展示；消息数并行统计，避免逐群串行拖慢响应
        List<long> allIds = knownGroupIds.Concat(enabledIds).Distinct().OrderBy(x => x).ToList();

        // 缓存缺名的群（通常是未启用过、Bot 从未处理过消息的群）实时向 napcat 查询并写入缓存
        List<long> missingNameIds = allIds.Where(id => !nameMap.ContainsKey(id)).ToList();
        if (missingNameIds.Count > 0)
        {
            GroupNameInfoDto?[] resolved = await Task.WhenAll(missingNameIds.Select(id => ResolveWithTimeoutAsync(manager, id)));
            foreach (GroupNameInfoDto? info in resolved)
            {
                if (info != null)
                {
                    nameMap[info.GroupId] = new GroupNameEntry
                    {
                        GroupId = info.GroupId,
                        Name = info.Name,
                        MemberCount = info.MemberCount,
                        MaxMemberCount = info.MaxMemberCount,
                        UpdatedTime = DateTime.Now,
                    };
                }
            }
        }

        int[] messageCounts = await Task.WhenAll(allIds.Select(historyRecorder.GetMessageCountByGroupIdAsync));
        int[] aiMessageCounts = await Task.WhenAll(allIds.Select(gid => historyRecorder.AiMessages.GetAiMessageCountBySessionKeyAsync(SessionKey.ToString(gid))));

        List<GroupEntryDto> entries = new(allIds.Count);
        for (int i = 0; i < allIds.Count; i++)
        {
            long groupId = allIds[i];
            nameMap.TryGetValue(groupId, out GroupNameEntry? entry);
            entries.Add(new GroupEntryDto(
                groupId,
                entry?.Name,
                entry?.MemberCount ?? 0,
                messageCounts[i],
                aiMessageCounts[i],
                enabledSet.Contains(groupId)));
        }

        return new GroupListDto(entries);
    }

    // 单次查询最多等 3 秒：napcat 未响应时快速返回，避免列表加载被拖住。
    // 查询抛错同样降级为 Name=null（群照常展示），不拖垮整个列表；
    // 超时后丢弃的 lookup 若随后 fault，用 continuation 吃掉异常，避免 UnobservedTaskException 噪音。
    private static async Task<GroupNameInfoDto?> ResolveWithTimeoutAsync(IGroupManager manager, long groupId)
    {
        // 调用本身也包进来：门面实现若同步抛错（连 Task 都没返回），同样降级
        Task<GroupNameInfoDto?>? lookup = null;
        try
        {
            lookup = manager.ResolveGroupNameAsync(groupId);
            Task completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(3)));
            if (completed == lookup)
            {
                return await lookup;
            }
        }
        catch (Exception exception)
        {
            SimpleLog.Default.Warn(exception, $"查询群 {groupId} 名称失败，降级展示");
        }
        if (lookup != null)
        {
            _ = lookup.ContinueWith(
                static task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
        }
        return null;
    }
}
