namespace HistoryWebFrontend;

/// <summary>与 /api/config 对应的配置快照;字段由后端反射 Config 类型动态生成。</summary>
public sealed record ConfigFieldDto(string TomlName, string Type, object? Value);
public sealed record ConfigSnapshotDto(IReadOnlyList<ConfigFieldDto> Fields, string VariablesToml);
public sealed record ConfigUpdateDto(Dictionary<string, object?> Fields, string VariablesToml);
