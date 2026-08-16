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
    /// <summary>
    /// 按前缀写入插件数据。<paramref name="prefix"/> 控制物理键的命名空间
    /// （如 "plugin" 或 "core"），默认 "plugin" 与既有插件数据保持一致。
    /// </summary>
    public async Task StorePluginData(string pluginName, object data, string prefix = "plugin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var pluginData = new PluginData { Id = $"{prefix}/{pluginName}", Value = data };
        await _pluginDataCollection.UpsertAsync(pluginData);
    }

    /// <summary>
    /// 按前缀读取插件数据；带前缀键不存在时回退到无前缀旧键，兼容此前写入的存量数据。
    /// </summary>
    public async Task<object?> GetPluginData(string pluginName, string prefix = "plugin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var pluginData = await _pluginDataCollection.FindByIdAsync($"{prefix}/{pluginName}");
        if (pluginData != null)
        {
            return pluginData.Value;
        }

        // 兼容此前 StorePluginData 写入的无前缀记录；下次保存时会迁移到带前缀键。
        var legacyData = await _pluginDataCollection.FindByIdAsync(pluginName);
        return legacyData?.Value;
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

    /// <summary>Plugin_Data_Table 物理集合名（插件对象数据）。</summary>
    private const string DataTableName = "Plugin_Data_Table";
    /// <summary>Plugin_Config_Table 物理集合名（插件配置）。</summary>
    private const string ConfigTableName = "Plugin_Config_Table";

    /// <summary>
    /// 读取 Plugin_Data_Table 全部原始文档（BsonDocument）。
    /// 以原始 Bson 形式返回，不反序列化 Value 字段：Value 带 LiteDB _type 元数据，
    /// 已删除插件（如 highlights）的类型不存在时强类型读取会抛 LiteException。
    /// 供 WebUI 高级配置面板展示/排查残留数据。
    /// </summary>
    public async Task<IReadOnlyList<BsonDocument>> GetRawDataEntriesAsync()
        => await GetRawEntriesAsync(DataTableName);

    /// <summary>读取 Plugin_Config_Table 全部原始文档（BsonDocument），语义同 <see cref="GetRawDataEntriesAsync"/>。</summary>
    public async Task<IReadOnlyList<BsonDocument>> GetRawConfigEntriesAsync()
        => await GetRawEntriesAsync(ConfigTableName);

    /// <summary>按 _id 删除 Plugin_Data_Table 中的一条原始记录；不存在返回 false。</summary>
    public Task<bool> DeleteRawDataEntryAsync(string id)
        => DeleteRawEntryAsync(DataTableName, id);

    /// <summary>按 _id 删除 Plugin_Config_Table 中的一条原始记录；不存在返回 false。</summary>
    public Task<bool> DeleteRawConfigEntryAsync(string id)
        => DeleteRawEntryAsync(ConfigTableName, id);

    private async Task<IReadOnlyList<BsonDocument>> GetRawEntriesAsync(string collectionName)
    {
        var collection = _db.GetCollection(collectionName);
        var docs = await collection.FindAllAsync();
        return docs.ToList();
    }

    private Task<bool> DeleteRawEntryAsync(string collectionName, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var collection = _db.GetCollection(collectionName);
        return collection.DeleteAsync(new BsonValue(id));
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
