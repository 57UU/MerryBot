using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace DataProvider;

public class PluginStorageDatabase : SQLiteDataProvider
{
    //sql
    private const string UPSERT_SQL = $"INSERT INTO {Str.PLUGIN_DATA_TABLE} ({Str.NAME}, {Str.VALUE}) VALUES (@PluginName, @Data) ON CONFLICT({Str.NAME}) DO UPDATE SET {Str.VALUE} = excluded.{Str.VALUE}";
    private const string SELECT_SQL = $"SELECT {Str.VALUE} FROM {Str.PLUGIN_DATA_TABLE} WHERE {Str.NAME}==@pluginName";
    
    public PluginStorageDatabase(string databasePath="plugin_data.db") : base(databasePath)
    {
        ExecuteSQLAsync(Str.Build_Table_SQL).Wait();
    }
    
    public async Task StorePluginData(string pluginName, string data)
    {
        await ExecutePreparedAsync(UPSERT_SQL, command => {
            command.Parameters.AddWithValue("@Data", data);
            command.Parameters.AddWithValue("@PluginName", pluginName);
        });
    }
    
    public async Task<string> GetPluginData(string pluginName)
    {
        // 直接使用预编译的SQL语句
        using var result = await ReadPreparedAsync(SELECT_SQL, command => {
            command.Parameters.AddWithValue("@pluginName", pluginName);
        });
        
        if (await result.ReadAsync()) {
            return (string)result[Str.VALUE];
        }
        return "";
    }

    private class Str
    {
        internal const string NAME = "Name";
        internal const string VALUE = "Value";
        internal const string PLUGIN_DATA_TABLE = "Plugin_Data_Table";
        internal const string Build_Table_SQL =
            $"CREATE TABLE IF NOT EXISTS {PLUGIN_DATA_TABLE} (" +
                $"{NAME} TEXT PRIMARY KEY," +
                $"{VALUE} TEXT DEFAULT ''" +
            $")";
    }
}
