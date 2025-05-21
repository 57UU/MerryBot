using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataProvider;

public class PluginStorageDatabase : SQLiteDataProvider
{
    public PluginStorageDatabase(string databasePath="plugin_data.db") : base(databasePath)
    {
        ExecuteSQLAsync(Str.Build_Table_SQL).Wait();
    }
    public async Task StorePluginData(string pluginName,string data)
    {
        var sql = $"UPDATE {Str.PLUGIN_DATA_TABLE} SET {Str.VALUE} = @Data WHERE {Str.NAME} = @PluginName";
        var command=BuildSQL(sql)
            .Add("@Data",data)
            .Add("@PluginName", pluginName);
        int rowAffected = await ExecuteSQLAsync(command);
        if (rowAffected == 0)
        {
            //insert
            sql = $"INSERT INTO {Str.PLUGIN_DATA_TABLE} ({Str.NAME}, {Str.VALUE}) VALUES (@PluginName, @Data)";
            command=BuildSQL(sql)
                .Add("@Data", data)
                .Add("@PluginName", pluginName);
            await ExecuteSQLAsync(command);
        }

    }
    public async Task<string> GetPluginData(string pluginName)
    {
        var sql = $"SELECT {Str.VALUE} FROM {Str.PLUGIN_DATA_TABLE} WHERE {Str.NAME}==@pluginName";
        var command=BuildSQL(sql)
            .Add("@pluginName",pluginName);
        var result=await ReadLineAsync(command);
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
