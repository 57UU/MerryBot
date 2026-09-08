using DataService;
using MerryBot.WebUI.Api;

namespace MerryBot.Test;

/// <summary>
/// GroupBrowseService：群列表拼装（fake IGroupManager + 临时空历史库）。
/// 已启用的群即使没有历史记录也展示；缓存缺名时经门面实时查询，失败/超时返回 null 时群仍展示（Name 为 null）。
/// </summary>
public sealed class GroupBrowseServiceTests
{
    private sealed class FakeGroupManager : IGroupManager
    {
        public IReadOnlyList<long> EnabledIds { get; set; } = [];
        public Func<long, Task<GroupNameInfoDto?>>? Resolver { get; set; }

        public IReadOnlyList<long> GetEnabledGroupIds() => EnabledIds;

        public Task AddGroupAsync(long groupId) => Task.CompletedTask;

        public Task RemoveGroupAsync(long groupId) => Task.CompletedTask;

        public Task<GroupNameInfoDto?> ResolveGroupNameAsync(long groupId)
            => Resolver?.Invoke(groupId) ?? Task.FromResult<GroupNameInfoDto?>(null);
    }

    private static string CreateDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "merrybot-grouptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "storage"));
        return directory;
    }

    [Fact]
    public async Task Null_Arguments_Throw()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GroupBrowseService.GetGroupsAsync(null!, history));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GroupBrowseService.GetGroupsAsync(new FakeGroupManager(), null!));
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Enabled_Group_Without_History_Is_Listed_As_Enabled()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            FakeGroupManager manager = new() { EnabledIds = [123456L] };

            GroupListDto list = await GroupBrowseService.GetGroupsAsync(manager, history);

            GroupEntryDto entry = Assert.Single(list.Groups);
            Assert.Equal(123456L, entry.GroupId);
            Assert.True(entry.Enabled);
            Assert.Null(entry.Name);
            Assert.Equal(0, entry.MessageCount);
            Assert.Equal(0, entry.AiMessageCount);
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Resolved_Name_Is_Shown_For_Cache_Miss()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            FakeGroupManager manager = new()
            {
                EnabledIds = [123456L],
                Resolver = groupId => Task.FromResult<GroupNameInfoDto?>(
                    new GroupNameInfoDto(groupId, "测试群", 10, 100)),
            };

            GroupListDto list = await GroupBrowseService.GetGroupsAsync(manager, history);

            GroupEntryDto entry = Assert.Single(list.Groups);
            Assert.Equal("测试群", entry.Name);
            Assert.Equal(10, entry.MemberCount);
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Groups_Are_Ordered_By_Id()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            FakeGroupManager manager = new() { EnabledIds = [999L, 111L, 555L] };

            GroupListDto list = await GroupBrowseService.GetGroupsAsync(manager, history);

            Assert.Equal([111L, 555L, 999L], list.Groups.Select(static entry => entry.GroupId).ToList());
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
