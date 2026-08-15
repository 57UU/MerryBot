using DataService;

namespace MerryBot.WebUI.Components.Shared;

/// <summary>
/// 群聊列表加载完成后的上下文，供页面获取群 ID 列表与群名缓存（页面头部可能需要展示群名/成员数）。
/// </summary>
public readonly record struct GroupListContext(
    IReadOnlyList<long> GroupIds,
    IReadOnlyDictionary<long, GroupNameEntry> GroupNames);
