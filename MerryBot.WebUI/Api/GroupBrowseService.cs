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

    /// <summary>批量实时查询并写入缓存；默认实现为并发单查扇出，Logic 用 get_group_list 覆盖为真批量。查不到的群不在字典中。</summary>
    async Task<Dictionary<long, GroupNameInfoDto>> ResolveGroupNamesAsync(IReadOnlyList<long> groupIds)
    {
        Dictionary<long, GroupNameInfoDto> result = new();
        if (groupIds.Count == 0)
        {
            return result;
        }
        GroupNameInfoDto?[] resolved = await Task.WhenAll(groupIds.Select(ResolveGroupNameAsync));
        foreach (GroupNameInfoDto? info in resolved)
        {
            if (info != null)
            {
                result[info.GroupId] = info;
            }
        }
        return result;
    }
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
        // 已启用的群即使还没有历史记录也一并展示；消息数并行统计，避免逐群串行拖慢响应
        List<long> allIds = knownGroupIds.Concat(enabledIds).Distinct().OrderBy(x => x).ToList();

        // 缓存缺名的群（通常是未启用过、Bot 从未处理过消息的群）实时向 napcat 查询并写入缓存
        Dictionary<long, GroupNameEntry> nameMap =
            await ResolveGroupNamesAsync(manager, historyRecorder, allIds);

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

    /// <summary>群名批量解析：先读本地缓存，缺名再经门面实时补齐；manager 为 null 时只读缓存。失败/超时降级为缺名，不抛。</summary>
    public static async Task<Dictionary<long, GroupNameEntry>> ResolveGroupNamesAsync(
        IGroupManager? manager, HistoryRecorder historyRecorder, IReadOnlyList<long> groupIds)
    {
        ArgumentNullException.ThrowIfNull(historyRecorder);
        ArgumentNullException.ThrowIfNull(groupIds);
        Dictionary<long, GroupNameEntry> nameMap = (await historyRecorder.GetAllGroupNamesAsync())
            .GroupBy(static item => item.GroupId)
            .ToDictionary(static group => group.Key, static group => group.First());
        List<long> missing = groupIds.Distinct().Where(id => !nameMap.ContainsKey(id)).ToList();
        if (missing.Count == 0 || manager == null)
        {
            return nameMap;
        }
        // 优先真批量；空结果/抛错/超时一律回退逐群单查扇出，保证与原来逐群行为一致
        Dictionary<long, GroupNameInfoDto> batched = await CallWithTimeoutAsync(() => manager.ResolveGroupNamesAsync(missing));
        List<long> stillMissing = missing.Where(id => !batched.ContainsKey(id)).ToList();
        if (stillMissing.Count > 0)
        {
            GroupNameInfoDto?[] singled = await Task.WhenAll(stillMissing.Select(id => ResolveWithTimeoutAsync(manager, id)));
            foreach (GroupNameInfoDto? info in singled)
            {
                if (info != null)
                {
                    batched[info.GroupId] = info;
                }
            }
        }
        foreach (KeyValuePair<long, GroupNameInfoDto> pair in batched)
        {
            nameMap[pair.Key] = new GroupNameEntry
            {
                GroupId = pair.Key,
                Name = pair.Value.Name,
                MemberCount = pair.Value.MemberCount,
                MaxMemberCount = pair.Value.MaxMemberCount,
                UpdatedTime = DateTime.Now,
            };
        }
        return nameMap;
    }

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    private static async Task<Dictionary<long, GroupNameInfoDto>> CallWithTimeoutAsync(
        Func<Task<Dictionary<long, GroupNameInfoDto>>> call)
    {
        Task<Dictionary<long, GroupNameInfoDto>>? task = null;
        try
        {
            task = call();
            Task completed = await Task.WhenAny(task, Task.Delay(ResolveTimeout));
            if (completed == task)
            {
                return await task;
            }
        }
        catch (Exception exception)
        {
            SimpleLog.Default.Warn(exception, "批量查询群名失败，回退单查");
        }
        if (task != null)
        {
            _ = task.ContinueWith(
                static inner => _ = inner.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
        }
        return new();
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
