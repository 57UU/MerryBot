using LiteDB;
using LiteDB.Async;
using System;
using System.Threading.Tasks;

namespace DataProvider;

public partial class PluginStorageDatabase : IDisposable
{
    private readonly LiteDatabaseAsync _db;
    private readonly ILiteCollectionAsync<PluginData> _collection;

    public PluginStorageDatabase(string databasePath = "plugin_data.db")
    {
        _db = new LiteDatabaseAsync(databasePath);
        _collection = _db.GetCollection<PluginData>("Plugin_Data_Table");
        _ = _collection.EnsureIndexAsync(x => x.Name);
    }

    public async Task StorePluginData(string pluginName, object data)
    {

        var pluginData = new PluginData
        {
            Id = pluginName,
            Name = pluginName,
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

    private class PluginData
    {
        [BsonId]
        public string Id { get; set; } = "";

        [BsonField("Name")]
        public string Name { get; set; } = "";

        [BsonField("Value")]
        public object Value { get; set; }
    }
}
