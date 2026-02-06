using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DataProvider;

public partial class PluginStorageDatabase
{
    private const string AI_MESSAGE_UPSERT_SQL =
        $"INSERT INTO {StrAiMessage.AiMessageDataTeble} ({StrAiMessage.GroupId}, {StrAiMessage.MessageType}, {StrAiMessage.Content}) VALUES (@GroupId, @MessageType, @Content)";

    public async Task RecordAiMessage(long groupId, string messageType, string content)
    {
        await ExecutePreparedAsync(AI_MESSAGE_UPSERT_SQL, command => {
            command.Parameters.AddWithValue("@GroupId", groupId);
            command.Parameters.AddWithValue("@MessageType", messageType);
            command.Parameters.AddWithValue("@Content", content);
        });
    }


    private static class StrAiMessage
    {
        internal const string GroupId = "Group_Id";
        internal const string MessageType = "Message_Type";
        internal const string Content = "Content";
        internal const string AiMessageDataTeble = "AI_Message_Data_Table";
        internal const string Build_Table_SQL =
            $"CREATE TABLE IF NOT EXISTS {AiMessageDataTeble} (" +
                $"Id INTEGER PRIMARY KEY AUTOINCREMENT," +
                $"{GroupId} INTEGER NOT NULL," +
                $"{MessageType} TEXT NOT NULL," +
                $"{Content} TEXT NOT NULL," +
            $")";
    }
}

public class AiMessage
{
    public long GroupId { get; set; }
    public string MessageType { get; set; } = "";
    public string Content { get; set; } = "";
}
