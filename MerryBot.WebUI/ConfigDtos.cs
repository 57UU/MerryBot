namespace MerryBot.WebUI;

/// <summary>与 /api/config 对应的动态配置面板数据。</summary>
public sealed record ConfigPanelDto(IReadOnlyList<ConfigSectionDto> Sections);
public sealed record ConfigSectionDto(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<ConfigFieldDto> Fields);
public sealed record ConfigFieldDto(
    string Key,
    string Name,
    string Description,
    string Type,
    object? Value,
    IReadOnlyList<string>? EnumOptions = null);
public sealed record ConfigUpdateDto(Dictionary<string, object?> Fields);
