using IdGen;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataService;

public class AiMessageRecorder : IDisposable
{
    private SqliteConnection dbConn;
    private readonly Dictionary<string, SqliteCommand> _preparedCommands = new Dictionary<string, SqliteCommand>();
    private const string AI_MESSAGE_UPSERT_SQL = 
        "INSERT INTO AI_Message_Data_Table (Id, Group_Id, Message_Type, Content, Time) VALUES (@Id, @GroupId, @MessageType, @Content, @Time)";
    private readonly IdGen.IdGenerator idGenerator;
    private readonly string _dbPath;
    
    public AiMessageRecorder(string databasePath = "ai_message.db",int machineCode=0)
    {
        _dbPath = databasePath;
        dbConn = new SqliteConnection($"Data Source={databasePath}");
        dbConn.Open();
        idGenerator = new IdGen.IdGenerator(machineCode,IdGenConfig.idGeneratorOptions);
        InitializeDatabase();
    }
    
    private void InitializeDatabase()
    {
        string createTableSql = @"
        CREATE TABLE IF NOT EXISTS AI_Message_Data_Table (
            Id INTEGER PRIMARY KEY,
            Group_Id INTEGER NOT NULL,
            Message_Type TEXT NOT NULL,
            Content TEXT NOT NULL,
            Time INTEGER NOT NULL
        );
        ";
        
        using var command = new SqliteCommand(createTableSql, dbConn);
        command.ExecuteNonQuery();
    }
    
    public async Task RecordAiMessage(long groupId, string messageType, string content)
    {
        var command = PrepareStatement(AI_MESSAGE_UPSERT_SQL);
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@Id", idGenerator.CreateId());
        command.Parameters.AddWithValue("@GroupId", groupId);
        command.Parameters.AddWithValue("@MessageType", messageType);
        command.Parameters.AddWithValue("@Content", content);
        command.Parameters.AddWithValue("@Time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        
        await command.ExecuteNonQueryAsync();
    }
    
    public async Task<List<(long Id, long GroupId, string MessageType, string Content, long Time)>> GetAiMessagesByGroupId(long groupId, int page = 1, int pageSize = 50)
    {
        var skip = (page - 1) * pageSize;
        var messages = new List<(long Id, long GroupId, string MessageType, string Content, long Time)>();
        
        string sql = "SELECT Id, Group_Id, Message_Type, Content, Time FROM AI_Message_Data_Table WHERE Group_Id = @GroupId ORDER BY Id DESC LIMIT @Limit OFFSET @Offset";
        using var command = new SqliteCommand(sql, dbConn);
        command.Parameters.AddWithValue("@GroupId", groupId);
        command.Parameters.AddWithValue("@Limit", pageSize);
        command.Parameters.AddWithValue("@Offset", skip);
        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            messages.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4)
            ));
        }
        
        return messages;
    }
    
    public async Task<int> GetAiMessageCountByGroupId(long groupId)
    {
        string sql = "SELECT COUNT(*) FROM AI_Message_Data_Table WHERE Group_Id = @GroupId";
        using var command = new SqliteCommand(sql, dbConn);
        command.Parameters.AddWithValue("@GroupId", groupId);
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
    
    public string GetDatabaseSize()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                var fileInfo = new FileInfo(_dbPath);
                return FormatFileSize(fileInfo.Length);
            }
            return "0 B";
        }
        catch
        {
            return "Unknown";
        }
    }
    
    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
    
    private SqliteCommand PrepareStatement(string sql)
    {
        if (_preparedCommands.TryGetValue(sql, out var existingCommand))
        {
            return existingCommand;
        }
        
        var command = new SqliteCommand(sql, dbConn);
        _preparedCommands[sql] = command;
        return command;
    }
    
    public void Close()
    {
        foreach (var command in _preparedCommands.Values)
        {
            command.Dispose();
        }
        _preparedCommands.Clear();
        
        dbConn.Close();
    }
    
    public void Dispose()
    {
        Close();
        dbConn.Dispose();
        GC.SuppressFinalize(this);
    }
}
