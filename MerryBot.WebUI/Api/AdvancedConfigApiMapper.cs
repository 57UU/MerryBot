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

        // id 走 query 参数：catch-all 路径参数对 %2F 不做 URL 解码（_id 形如 "plugin/agent" 含斜杠），
        // 且 404 会被 UseStatusCodePagesWithReExecute 以原方法重执行到 /not-found 产生 405；
        // 删除统一返回 204（记录不存在视为已删除），不触发 404。
        group.MapPost("/data/delete", (string id) => DeleteRawEntry(database.DeleteRawDataEntryAsync, id));
        group.MapPost("/config/delete", (string id) => DeleteRawEntry(database.DeleteRawConfigEntryAsync, id));
    }

    /// <summary>把原始 BsonDocument 序列化为 pretty JSON 字符串，供页面直接展示。</summary>
    private static RawEntryDto ToDto(BsonDocument doc)
        => new(doc["_id"].AsString, JsonSerializer.Serialize(doc, true));

    /// <summary>执行删除；删除成功或记录不存在均返回 204（幂等），避免 404 触发状态码重执行。</summary>
    private static async Task<IResult> DeleteRawEntry(Func<string, Task<bool>> delete, string id)
    {
        await delete(id);
        return Results.NoContent();
    }
}

/// <summary>一条原始数据：Id 为 LiteDB _id（删除用），Bson 为文档的 pretty JSON。</summary>
public sealed record RawEntryDto(string Id, string Bson);
