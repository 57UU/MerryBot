using System.Reflection;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace MerryBot.Api;

/// <summary>
/// 配置字段校验规则,通过特性声明在配置属性上,由 <see cref="ConfigValidator"/> 反射读取。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigRuleAttribute : Attribute
{
    /// <summary>字符串值不能为空。</summary>
    public bool Required { get; init; }
    /// <summary>URL scheme 白名单,逗号分隔(如 "ws,wss"),仅对字符串属性生效。</summary>
    public string? Scheme { get; init; }
    /// <summary>数值(或列表中的每个元素)必须为正数。</summary>
    public bool Positive { get; init; }
}

/// <summary>
/// 通过反射读取配置对象属性上的 <see cref="ConfigRuleAttribute"/>,校验配置准确性。
/// 与具体配置类型解耦,只依赖属性特性。
/// </summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(object config)
    {
        var errors = new List<string>();
        foreach (var property in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var rule = property.GetCustomAttribute<ConfigRuleAttribute>();
            if (rule is null) continue;

            var tomlName = property.GetCustomAttribute<TomlPropertyNameAttribute>()?.Name ?? property.Name;
            var value = property.GetValue(config);

            if (rule.Required && value is string s && string.IsNullOrWhiteSpace(s))
            {
                errors.Add($"`{tomlName}` 不能为空");
            }

            if (!string.IsNullOrWhiteSpace(rule.Scheme) && value is string url)
            {
                var schemes = rule.Scheme.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                    || !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"`{tomlName}` 必须是 {rule.Scheme} 开头的合法 URL");
                }
            }

            if (rule.Positive && value is not string)
            {
                if (value is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (Convert.ToInt64(item) <= 0)
                        {
                            errors.Add($"`{tomlName}` 中的每一项必须为正数");
                            break;
                        }
                    }
                }
                else if (Convert.ToInt64(value) <= 0)
                {
                    errors.Add($"`{tomlName}` 必须为正数");
                }
            }
        }
        return errors;
    }
}
