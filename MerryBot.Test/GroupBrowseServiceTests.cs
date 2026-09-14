using BotPlugin;
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

    private static GroupMessage NewMessage(long groupId, long messageId)
        => new()
        {
            MessageId = messageId,
            GroupId = groupId,
            SenderId = 10001L,
            SenderNickname = "测试用户",
            SenderGroupNickname = string.Empty,
            SenderGroupRole = string.Empty,
            Messages = [],
            Time = DateTime.UtcNow,
        };

    [Fact]
    public async Task History_Stats_And_Cached_Names_Are_Merged()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            // 群 111：2 条历史消息 + 1 条 AI 消息 + 群名缓存；群 222：1 条历史消息，无缓存、无启用
            await history.RecordMessageAsync(NewMessage(111L, 1L));
            await history.RecordMessageAsync(NewMessage(111L, 2L));
            await history.RecordMessageAsync(NewMessage(222L, 1L));
            await history.AiMessages.RecordAiMessageAsync(SessionKey.ToString(111L), "user", "hello");
            await history.RecordOrUpdateGroupNameAsync(new GroupNameEntry
            {
                GroupId = 111L,
                Name = "缓存群",
                MemberCount = 5,
                MaxMemberCount = 50,
                UpdatedTime = DateTime.UtcNow,
            });
            FakeGroupManager manager = new() { EnabledIds = [111L] };

            GroupListDto list = await GroupBrowseService.GetGroupsAsync(manager, history);

            Assert.Equal([111L, 222L], list.Groups.Select(static entry => entry.GroupId).ToList());
            GroupEntryDto cached = list.Groups[0];
            Assert.Equal("缓存群", cached.Name);
            Assert.Equal(5, cached.MemberCount);
            Assert.Equal(2, cached.MessageCount);
            Assert.Equal(1, cached.AiMessageCount);
            Assert.True(cached.Enabled);
            GroupEntryDto uncached = list.Groups[1];
            Assert.Null(uncached.Name);
            Assert.Equal(1, uncached.MessageCount);
            Assert.Equal(0, uncached.AiMessageCount);
            Assert.False(uncached.Enabled);
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Resolver_Exception_Degrades_To_Null_Name()
    {
        string directory = CreateDir();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        try
        {
            // napcat 查询抛错（未连接/抖动）时群仍展示，Name 为 null，不拖垮整个列表
            FakeGroupManager manager = new()
            {
                EnabledIds = [123456L],
                Resolver = _ => throw new InvalidOperationException("napcat 未连接"),
            };

            GroupListDto list = await GroupBrowseService.GetGroupsAsync(manager, history);

            GroupEntryDto entry = Assert.Single(list.Groups);
            Assert.Equal(123456L, entry.GroupId);
            Assert.Null(entry.Name);
            Assert.True(entry.Enabled);
        }
        finally
        {
            history.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
