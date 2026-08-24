using BotPlugin;
using DataService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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

/// <summary>群聊管理 API：列出有历史记录或已启用的群，以及启用/禁用操作。</summary>
public static class GroupApiMapper
{
    public static void Map(WebApplication app, IGroupManager manager, HistoryRecorder historyRecorder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(historyRecorder);

        var group = app.MapGroup("/api/groups");
        group.MapGet("/", async () =>
        {
            var knownGroupIds = await historyRecorder.GetAllGroupIdsAsync();
            var enabledIds = manager.GetEnabledGroupIds().OrderBy(x => x).ToList();
            var enabledSet = enabledIds.ToHashSet();
            var names = await historyRecorder.GetAllGroupNamesAsync();
            var nameMap = names.ToDictionary(x => x.GroupId);

            // 已启用的群即使还没有历史记录也一并展示；消息数并行统计，避免逐群串行拖慢响应
            var allIds = knownGroupIds.Concat(enabledIds).Distinct().OrderBy(x => x).ToList();

            // 缓存缺名的群（通常是未启用过、Bot 从未处理过消息的群）实时向 napcat 查询并写入缓存
            var missingNameIds = allIds.Where(id => !nameMap.ContainsKey(id)).ToList();
            if (missingNameIds.Count > 0)
            {
                var resolved = await Task.WhenAll(missingNameIds.Select(ResolveWithTimeoutAsync));
                foreach (var info in resolved)
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

            var messageCounts = await Task.WhenAll(allIds.Select(historyRecorder.GetMessageCountByGroupIdAsync));
            var aiMessageCounts = await Task.WhenAll(allIds.Select(gid => historyRecorder.AiMessages.GetAiMessageCountBySessionKeyAsync(SessionKey.ToString(gid))));

            var entries = new List<GroupEntryDto>(allIds.Count);
            for (var i = 0; i < allIds.Count; i++)
            {
                var groupId = allIds[i];
                nameMap.TryGetValue(groupId, out var entry);
                entries.Add(new GroupEntryDto(
                    groupId,
                    entry?.Name,
                    entry?.MemberCount ?? 0,
                    messageCounts[i],
                    aiMessageCounts[i],
                    enabledSet.Contains(groupId)));
            }

            return Results.Ok(new GroupListDto(entries));

            // 单次查询最多等 3 秒：napcat 未响应时快速返回，避免列表接口被拖住
            async Task<GroupNameInfoDto?> ResolveWithTimeoutAsync(long groupId)
            {
                var lookup = manager.ResolveGroupNameAsync(groupId);
                var completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(3)));
                return completed == lookup ? await lookup : null;
            }
        });

        group.MapPost("/{groupId:long}", async (long groupId) =>
        {
            if (groupId <= 0) return Results.BadRequest("群号无效。");
            await manager.AddGroupAsync(groupId);
            return Results.NoContent();
        });

        group.MapPost("/{groupId:long}/delete", async (long groupId) =>
        {
            if (groupId <= 0) return Results.BadRequest("群号无效。");
            await manager.RemoveGroupAsync(groupId);
            return Results.NoContent();
        });
    }
}
