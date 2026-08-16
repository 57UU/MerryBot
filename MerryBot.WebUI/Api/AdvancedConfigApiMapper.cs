using DataProvider;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MerryBot.WebUI.Api;

/// <summary>
/// 高级配置面板 API：以原始 BSON（JSON）查看 / 删除 Plugin_Data_Table 与 Plugin_Config_Table 的条目。
/// 全部走原始 BsonDocument 路径，不反序列化 Value 字段，已删除插件的残留数据（_type 元数据
/// 指向不存在类型）也能正常展示与删除，用于排查/清理这类数据。
/// </summary>
public static class AdvancedConfigApiMapper
{
    public static void Map(WebApplication app, PluginStorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(database);

        var group = app.MapGroup("/api/advanced");

        group.MapGet("/data", async () =>
            Results.Ok((await database.GetRawDataEntriesAsync()).Select(ToDto)));
        group.MapGet("/config", async () =>
            Results.Ok((await database.GetRawConfigEntriesAsync()).Select(ToDto)));

        // _id 形如 "plugin/agent"（含斜杠），使用 catch-all 参数承载
        group.MapDelete("/data/{**id}", async (string id) =>
            await database.DeleteRawDataEntryAsync(id) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/config/{**id}", async (string id) =>
            await database.DeleteRawConfigEntryAsync(id) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>把原始 BsonDocument 序列化为 pretty JSON 字符串，供页面直接展示。</summary>
    private static RawEntryDto ToDto(BsonDocument doc)
        => new(doc["_id"].AsString, JsonSerializer.Serialize(doc, true));
}

/// <summary>一条原始数据：Id 为 LiteDB _id（删除用），Bson 为文档的 pretty JSON。</summary>
public sealed record RawEntryDto(string Id, string Bson);
