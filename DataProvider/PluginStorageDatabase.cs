using LiteDB;
using LiteDB.Async;

namespace DataProvider;

public partial class PluginStorageDatabase : IDisposable
{
    private readonly LiteDatabaseAsync _db;
    private readonly ILiteCollectionAsync<PluginData> _collection;

    public PluginStorageDatabase(string databasePath = "plugin_data.db")
    {
        var mapper = new BsonMapper
        {
            IncludeFields = true
        };

        _db = new LiteDatabaseAsync(databasePath, mapper);
        _collection = _db.GetCollection<PluginData>("Plugin_Data_Table");
        _ = _collection.EnsureIndexAsync(x => x.Id);
    }

    public async Task StorePluginData(string pluginName, object data)
    {

        var pluginData = new PluginData
        {
            Id = pluginName,
            Value = data
        };
        await _collection.UpsertAsync(pluginData);

    }

    public async Task<object?> GetPluginData(string pluginName)
    {

        var pluginData = await _collection.FindByIdAsync(pluginName);
        return pluginData?.Value;

    }

    public void Dispose()
    {
        _db?.Dispose();
    }

#pragma warning disable CS8618 
    private class PluginData
    {
        [BsonId]
        public string Id { get; set; } = "";

        [BsonField("Value")]
        public object Value { get; set; }
    }
}

#pragma warning restore CS8618