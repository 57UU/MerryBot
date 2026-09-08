namespace MerryBot.WebUI.Api;

/// <summary>插件库原始 BSON 条目展示形态（高级配置页排查残留数据用）。</summary>
public sealed record RawEntryDto(string Id, string Bson);
