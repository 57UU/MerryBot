using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataProvider;

public class SQLiteDataProvider
{
    protected SqliteConnection dbConn;
    public SQLiteDataProvider(string databaseName)
    {

        dbConn = new($"Data Source={databaseName}");
        sqlBuilder = new SQLBuilder(dbConn);
        dbConn.Open();
    }

    public void Close()
    {
        dbConn.Close();
    }

    public void Flush()
    {
       
    }
    protected Task<int> ExecuteSQLAsync(string sql)
    {
        var command = new SqliteCommand(sql, dbConn);
        return ExecuteSQLAsync(command);


    }
    protected async Task<int> ExecuteSQLAsync(SqliteCommand command)
    {
        try
        {
            return await command.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            throw new DatabaseException("Server SQL exception",e);
        }

    }
    protected Task<int> ExecuteSQLAsync(_builder builder)
    {
        return ExecuteSQLAsync(builder.Command);
    }
    protected Task<SqliteDataReader> ReadLineAsync(string sql)
    {
        SqliteCommand command = new SqliteCommand(sql, dbConn);
        return ReadLineAsync(command);
    }
    protected Task<SqliteDataReader> ReadLineAsync(_builder builder)
    {
        return ReadLineAsync(builder.Command);
    }

    protected Task<SqliteDataReader> ReadLineAsync(SqliteCommand command)
    {
        return command.ExecuteReaderAsync();
    }

    protected Task<SqliteDataReader> ReadOneLineAsync(string sql)
    {
        return ReadOneLineAsync(new SqliteCommand(sql, dbConn));
    }
    protected async Task<SqliteDataReader> ReadOneLineAsync(SqliteCommand command)
    {
        var a =await ReadLineAsync(command);
        await a.ReadAsync();
        return a;
    }
    protected Task<SqliteDataReader> ReadOneLineAsync(_builder builder)
    {
        return ReadOneLineAsync(builder.Command);
    }
    public async Task RemoveAll()
    {
        GetAllTables().GetAsyncEnumerator().Current.ToArray();
        List<Task> tasks = new();
        await foreach(var table in  GetAllTables())
        {
            tasks.Add(ExecuteSQLAsync($"DELETE FROM {table}"));
        }
        await Task.WhenAll(tasks.ToArray());
    }
    protected async IAsyncEnumerable<string> GetAllTables()
    {
        var all_tables = "SELECT name FROM sqlite_master WHERE type='table' order by name";
        var result =await ReadLineAsync(all_tables);
        while (await result.ReadAsync())
        {
            yield return result["name"].ToString()!;
        }

    }
    protected async Task<bool> IsTableExists(string tableName)
    {
        var sql = $"SELECT name FROM sqlite_master WHERE name='{tableName}' AND type='table'";
        return await (await ReadLineAsync(sql)).ReadAsync();
    }
    protected SQLBuilder sqlBuilder;


    protected _builder BuildSQL(string sql)
    {
        return sqlBuilder.build(sql);
    }
}
public class SQLBuilder
{
    SqliteConnection conn;
    public SQLBuilder(SqliteConnection dbConn)
    {
        this.conn = dbConn;
    }
    public _builder build(string sql)
    {
        return new _builder(sql, conn);
    }

}
public class _builder
{
    public SqliteCommand Command { get; private set; }
    public _builder(string sql, SqliteConnection connection)
    {
        Command = new SqliteCommand(sql, connection);
    }
    public _builder Add(string parameter, object value)
    {
        Command.Parameters.AddWithValue(parameter, value);
        return this;
    }

}
public class DatabaseException : Exception
{
    public DatabaseException(string message,Exception? inner=null) : base(message,inner) { }
}