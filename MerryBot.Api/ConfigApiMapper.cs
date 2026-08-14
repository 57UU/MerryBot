using CommonLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace MerryBot.Api;

/// <summary>
/// 将配置类型通过反射暴露为 REST API,供历史后台「配置」页动态渲染表单并保存。
/// 与具体配置类型解耦:按 <paramref name="configType"/> 反射扫描属性生成字段 schema,
/// 通过注入的委托读取实例、保存与关闭程序。新增属性无需改动前端即可自动出现表单项;
/// 保存前调用 <see cref="ConfigValidator"/> 校验配置准确性。
/// </summary>
public static class ConfigApiMapper
{
    private const string VariablesTomlKey = "variables";

    public static void Map(
        WebApplication app,
        Type configType,
        Func<object> getInstance,
        Func<Task> save,
        Action<int> shutdown)
    {
        var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var variablesProperty = properties.FirstOrDefault(p => GetTomlName(p) == VariablesTomlKey);

        var group = app.MapGroup("/api/config");

        // 读取配置快照:反射生成的字段 schema + [variables] 的原始 TOML
        group.MapGet("/", () =>
        {
            var instance = getInstance();
            return Results.Ok(BuildSnapshot(properties, variablesProperty, instance));
        });

        // 应用修改并保存,保存前先在校验副本上校验准确性
        group.MapPut("/", async (ConfigUpdateDto body) =>
        {
            try
            {
                var current = getInstance();
                var candidate = CloneConfig(properties, current);
                ApplyTo(properties, variablesProperty, candidate, body);
                var errors = ConfigValidator.Validate(candidate);
                if (errors.Count > 0)
                {
                    return Text("配置校验未通过:\n" + string.Join("\n", errors));
                }
                ApplyTo(properties, variablesProperty, current, body);
                await save();
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Text(ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
            {
                return Text("配置值格式不正确:" + ex.Message);
            }
        });

        // 重启程序(重新编译后重启):先返回响应,延迟 500ms 后调用 shutdown,让响应有时间刷出
        group.MapPost("/restart", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                shutdown(ExitCode.RESTART);
            });
            return Results.Ok(new { restarting = true });
        });

        // 重载程序(不重新编译,仅重启)
        group.MapPost("/reload", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                shutdown(ExitCode.RELOAD);
            });
            return Results.Ok(new { restarting = true });
        });
    }

    private static IResult Text(string message)
        => Results.Text(message, "text/plain; charset=utf-8", statusCode: 400);

    private static ConfigSnapshotDto BuildSnapshot(PropertyInfo[] properties, PropertyInfo? variablesProperty, object instance)
    {
        var fields = new List<ConfigFieldDto>();
        foreach (var property in properties)
        {
            if (property == variablesProperty) continue;
            fields.Add(new ConfigFieldDto(GetTomlName(property), GetTypeName(property.PropertyType), property.GetValue(instance)));
        }
        return new ConfigSnapshotDto(fields, SerializeVariables(GetVariables(variablesProperty, instance)));
    }

    private static object CloneConfig(PropertyInfo[] properties, object instance)
    {
        var target = Activator.CreateInstance(instance.GetType())!;
        foreach (var property in properties)
        {
            property.SetValue(target, CloneValue(property.GetValue(instance)));
        }
        return target;
    }

    private static object? CloneValue(object? value)
        => value switch
        {
            List<long> list => new List<long>(list),
            Dictionary<string, TomlTable> dict => new Dictionary<string, TomlTable>(dict),
            _ => value
        };

    private static void ApplyTo(PropertyInfo[] properties, PropertyInfo? variablesProperty, object target, ConfigUpdateDto body)
    {
        foreach (var property in properties)
        {
            if (property == variablesProperty) continue;
            if (body.Fields.TryGetValue(GetTomlName(property), out var element))
            {
                property.SetValue(target, ConvertValue(property.PropertyType, element));
            }
        }
        if (variablesProperty != null)
        {
            variablesProperty.SetValue(target, ParseVariables(body.VariablesToml));
        }
    }

    private static Dictionary<string, TomlTable> GetVariables(PropertyInfo? variablesProperty, object instance)
        => variablesProperty?.GetValue(instance) as Dictionary<string, TomlTable> ?? new();

    private static object? ConvertValue(Type propertyType, JsonElement element)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(string))
        {
            return element.ValueKind == JsonValueKind.Null ? null : element.GetString();
        }
        if (type == typeof(bool))
        {
            return element.GetBoolean();
        }
        if (type == typeof(long))
        {
            return element.GetInt64();
        }
        if (type == typeof(int))
        {
            return element.GetInt32();
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return ConvertList(type, element);
        }
        throw new ArgumentException($"不支持的字段类型:{type.Name}");
    }

    private static object ConvertList(Type listType, JsonElement element)
    {
        var elementType = listType.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(Convert.ChangeType(item.GetInt64(), elementType));
        }
        return list;
    }

    private static Dictionary<string, TomlTable> ParseVariables(string? toml)
    {
        var result = new Dictionary<string, TomlTable>();
        if (string.IsNullOrWhiteSpace(toml)) return result;

        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(toml, new TomlSerializerOptions())!;
        }
        catch (Exception ex)
        {
            throw new ArgumentException("variables TOML 解析失败:" + ex.Message);
        }
        foreach (var kv in table)
        {
            if (kv.Value is not TomlTable nested)
            {
                throw new ArgumentException($"variables 中的 `{kv.Key}` 必须是表(形如 `[{kv.Key}]` 的小节),不能是普通键值");
            }
            result[kv.Key] = nested;
        }
        return result;
    }

    private static string SerializeVariables(Dictionary<string, TomlTable> variables)
    {
        if (variables.Count == 0) return string.Empty;
        var table = new TomlTable();
        foreach (var kv in variables)
        {
            table[kv.Key] = kv.Value;
        }
        return TomlSerializer.Serialize(table, new TomlSerializerOptions());
    }

    private static string GetTomlName(PropertyInfo property)
        => property.GetCustomAttribute<TomlPropertyNameAttribute>()?.Name ?? property.Name;

    private static string GetTypeName(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(long) || type == typeof(int)) return "long";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return "list";
        return "unknown";
    }

    private sealed record ConfigFieldDto(string TomlName, string Type, object? Value);
    private sealed record ConfigSnapshotDto(IReadOnlyList<ConfigFieldDto> Fields, string VariablesToml);
    private sealed record ConfigUpdateDto(Dictionary<string, JsonElement> Fields, string VariablesToml);
}
