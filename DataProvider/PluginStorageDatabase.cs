using LiteDB;
using LiteDB.Async;

namespace DataProvider;

public partial class PluginStorageDatabase : IDisposable
{
    private readonly LiteDatabaseAsync _db;
    private readonly ILiteCollectionAsync<PluginData> _pluginCollection;
    private readonly ILiteCollectionAsync<GroupPluginData> _groupCollection;

    public PluginStorageDatabase(string databasePath = "plugin_data.db")
    {
        var mapper = new BsonMapper { IncludeFields = true };
        _db = new LiteDatabaseAsync(databasePath, mapper);

        _pluginCollection = _db.GetCollection<PluginData>("Plugin_Data_Table");
        _ = _pluginCollection.EnsureIndexAsync(x => x.Id);

        _groupCollection = _db.GetCollection<GroupPluginData>("Group_Plugin_Data_Table");
        _ = _groupCollection.EnsureIndexAsync(x => x.Key);
    }

    // Plugin-level
    public async Task StorePluginData(string pluginName, object data)
    {
        var pluginData = new PluginData { Id = pluginName, Value = data };
        await _pluginCollection.UpsertAsync(pluginData);
    }

    public async Task<object?> GetPluginData(string pluginName)
    {
        var pluginData = await _pluginCollection.FindByIdAsync(pluginName);
        return pluginData?.Value;
    }

    // Group-level
    private static string MakeGroupKey(string pluginName, long groupId) => $"{pluginName}_{groupId}";

    public async Task StoreGroupPluginData(string pluginName, long groupId, object data)
    {
        var groupData = new GroupPluginData
        {
            Key = MakeGroupKey(pluginName, groupId),
            PluginName = pluginName,
            GroupId = groupId,
            Value = data
        };
        await _groupCollection.UpsertAsync(groupData);
    }

    public async Task<object?> GetGroupPluginData(string pluginName, long groupId)
    {
        var groupData = await _groupCollection.FindByIdAsync(MakeGroupKey(pluginName, groupId));
        return groupData?.Value;
    }

    public async Task DeleteGroupPluginData(string pluginName, long groupId)
    {
        await _groupCollection.DeleteAsync(MakeGroupKey(pluginName, groupId));
    }

    public void Dispose() => _db?.Dispose();

#pragma warning disable CS8618
    private class PluginData
    {
        [BsonId]
        public string Id { get; set; } = "";
        [BsonField("Value")]
        public object Value { get; set; } = null!;
    }

    private class GroupPluginData
    {
        [BsonId]
        public string Key { get; set; } = "";
        [BsonField("PluginName")]
        public string PluginName { get; set; } = "";
        [BsonField("GroupId")]
        public long GroupId { get; set; }
        [BsonField("Value")]
        public object Value { get; set; } = null!;
    }
#pragma warning restore CS8618
}