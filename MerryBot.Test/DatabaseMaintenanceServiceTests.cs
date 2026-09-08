using DataProvider;
using DataService;
using MerryBot.WebUI.Api;

namespace MerryBot.Test;

/// <summary>
/// DatabaseMaintenanceService：数据库大小查询与 Rebuild（临时真库）。
/// 坏 target 抛 ArgumentException；target 大小写/首尾空格被归一化。
/// </summary>
public sealed class DatabaseMaintenanceServiceTests
{
    private static string CreateDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "merrybot-dbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "storage"));
        return directory;
    }

    private static async Task<(PluginStorageDatabase pluginDb, HistoryRecorder history)> OpenDatabasesAsync(string directory)
    {
        PluginStorageDatabase pluginDb = new(Path.Combine(directory, "plugin_data.db"));
        await pluginDb.MigrateAsync();
        HistoryRecorder history = new(Path.Combine(directory, "group_history.db"), Path.Combine(directory, "storage"));
        await history.MigrateAsync();
        return (pluginDb, history);
    }

    [Fact]
    public async Task Invalid_Target_Throws_ArgumentException()
    {
        string directory = CreateDir();
        (PluginStorageDatabase pluginDb, HistoryRecorder history) = await OpenDatabasesAsync(directory);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DatabaseMaintenanceService.RebuildAsync(pluginDb, history, "nope"));
        }
        finally
        {
            history.Dispose();
            pluginDb.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Target_Is_Trimmed_And_Case_Insensitive()
    {
        string directory = CreateDir();
        (PluginStorageDatabase pluginDb, HistoryRecorder history) = await OpenDatabasesAsync(directory);
        try
        {
            // " ALL " 归一化为 all，不抛且返回两库结果
            IReadOnlyList<RebuildResultDto> results =
                await DatabaseMaintenanceService.RebuildAsync(pluginDb, history, " ALL ");

            Assert.Equal(2, results.Count);
        }
        finally
        {
            history.Dispose();
            pluginDb.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Rebuild_All_Returns_Both_Databases()
    {
        string directory = CreateDir();
        (PluginStorageDatabase pluginDb, HistoryRecorder history) = await OpenDatabasesAsync(directory);
        try
        {
            IReadOnlyList<RebuildResultDto> results =
                await DatabaseMaintenanceService.RebuildAsync(pluginDb, history, "all");

            Assert.Equal(2, results.Count);
            Assert.Equal("plugin", results[0].Target);
            Assert.Equal("history", results[1].Target);
            Assert.True(results[0].BeforeBytes > 0);
            Assert.True(results[1].BeforeBytes > 0);
        }
        finally
        {
            history.Dispose();
            pluginDb.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Rebuild_Single_Target_Returns_One_Result()
    {
        string directory = CreateDir();
        (PluginStorageDatabase pluginDb, HistoryRecorder history) = await OpenDatabasesAsync(directory);
        try
        {
            IReadOnlyList<RebuildResultDto> results =
                await DatabaseMaintenanceService.RebuildAsync(pluginDb, history, "plugin");

            RebuildResultDto single = Assert.Single(results);
            Assert.Equal("plugin", single.Target);
            Assert.True(single.ReducedSize.EndsWith(" (已释放)") || single.ReducedSize.EndsWith(" (增大)"));
        }
        finally
        {
            history.Dispose();
            pluginDb.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GetSizes_Reports_Both_Files()
    {
        string directory = CreateDir();
        (PluginStorageDatabase pluginDb, HistoryRecorder history) = await OpenDatabasesAsync(directory);
        try
        {
            await DatabaseMaintenanceService.RebuildAsync(pluginDb, history, "all");

            DatabaseSizesDto sizes = DatabaseMaintenanceService.GetSizes(pluginDb, history);

            Assert.True(sizes.PluginDbBytes > 0);
            Assert.True(sizes.HistoryDbBytes > 0);
            Assert.NotEmpty(sizes.PluginDbSize);
            Assert.NotEmpty(sizes.HistoryDbSize);
        }
        finally
        {
            history.Dispose();
            pluginDb.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
