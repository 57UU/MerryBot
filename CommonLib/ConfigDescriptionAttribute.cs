namespace CommonLib;

/// <summary>
/// 提供 WebUI 配置面板所需的显示名称与说明。
/// 可标注配置类和其公开配置属性。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ConfigDescriptionAttribute(string name, string description) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("配置名称不能为空。", nameof(name))
        : name;

    public string Description { get; } = description ?? string.Empty;
}
