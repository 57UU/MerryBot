using LiteDB;
using LiteDB.Async;

namespace DataProvider;

public partial class PluginStorageDatabase : IDisposable
{
    private readonly LiteDatabaseAsync _db;
    private readonly ILiteCollectionAsync<PluginData> _pluginDataCollection;
    private readonly ILiteCollectionAsync<PluginData> _groupConfigCollection;

    public PluginStorageDatabase(string databasePath = "plugin_data.db")
    {
        var mapper = new BsonMapper { IncludeFields = true };
        _db = new LiteDatabaseAsync(databasePath, mapper);

        _pluginDataCollection = _db.GetCollection<PluginData>("Plugin_Data_Table");
        _ = _pluginDataCollection.EnsureIndexAsync(x => x.Id);

        _groupConfigCollection = _db.GetCollection<PluginData>("Plugin_Config_Table");
        _ = _groupConfigCollection.EnsureIndexAsync(x => x.Id);
    }

    /// <summary>
    /// Creates a database view that can only access collections owned by the specified plugin.
    /// prefix 控制物理集合名的命名空间（如 "plugin" 或 "core"），默认 "plugin" 保持插件数据兼容。
    /// </summary>
    public PluginDatabaseScope CreateScope(string pluginId, string prefix = "plugin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new PluginDatabaseScope(_db, pluginId, prefix);
    }

    // Plugin-level
    public async Task StorePluginData(string pluginName, object data)
    {
        var pluginData = new PluginData { Id = pluginName, Value = data };
        await _pluginDataCollection.UpsertAsync(pluginData);
    }

    public async Task<object?> GetPluginData(string pluginName)
    {
        var pluginData = await _pluginDataCollection.FindByIdAsync(pluginName);
        return pluginData?.Value;
    }


    public async Task SetPluginConfig(string pluginName, dynamic config, string prefix = "plugin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var configData = new PluginData
        {
            Id = $"{prefix}/{pluginName}",
            Value = config
        };
        await _groupConfigCollection.UpsertAsync(configData);
    }

    public async Task<object?> GetPluginConfig(string pluginName, string prefix = "plugin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var configData = await _groupConfigCollection.FindByIdAsync($"{prefix}/{pluginName}");
        if (configData != null)
        {
            return configData.Value;
        }

        // 兼容此前 SetPluginConfig 写入的无前缀记录；下次保存时会迁移到规范键。
        var legacyData = await _groupConfigCollection.FindByIdAsync(pluginName);
        return legacyData?.Value;
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
#pragma warning restore CS8618
}
