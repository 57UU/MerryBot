using MerryBot.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MerryBot.WebUI.Api;

/// <summary>
/// 保存可由 WebUI 动态编辑的配置对象及其各自的持久化回调。
/// </summary>
public sealed class ConfigRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, RegisteredConfig> configurations = new(StringComparer.Ordinal);
    private readonly ILogger logger;

    public ConfigRegistry(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RegisterConfig(string id, object configuration, Func<Task> onSave)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(onSave);

        var registered = RegisteredConfig.Create(id, configuration, onSave, logger);
        lock (syncRoot)
        {
            if (!configurations.TryAdd(id, registered))
            {
                throw new InvalidOperationException($"配置已注册: {id}");
            }
        }
    }

    public IReadOnlyList<ConfigSectionDto> GetSnapshot()
    {
        RegisteredConfig[] snapshot;
        lock (syncRoot)
        {
            snapshot = configurations.Values.ToArray();
        }
        return snapshot.Select(static configuration => configuration.ToDto()).ToList();
    }

    public async Task SaveAsync(string id, IReadOnlyDictionary<string, JsonElement> fields)
    {
        RegisteredConfig configuration;
        lock (syncRoot)
        {
            configuration = configurations.GetValueOrDefault(id)
                ?? throw new KeyNotFoundException($"未找到配置: {id}");
        }
        await configuration.SaveAsync(fields);
    }

    private sealed class RegisteredConfig
    {
        private readonly object configuration;
        private readonly Func<Task> onSave;
        private readonly IReadOnlyList<ConfigProperty> properties;
        private readonly Dictionary<string, ConfigProperty> propertiesByKey;
        private readonly SemaphoreSlim saveLock = new(1, 1);

        private RegisteredConfig(
            string id,
            object configuration,
            Func<Task> onSave,
            string name,
            string description,
            IReadOnlyList<ConfigProperty> properties)
        {
            Id = id;
            this.configuration = configuration;
            this.onSave = onSave;
            Name = name;
            Description = description;
            this.properties = properties;
            propertiesByKey = properties.ToDictionary(property => property.Key, StringComparer.Ordinal);
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }

        public static RegisteredConfig Create(string id, object configuration, Func<Task> onSave, ILogger logger)
        {
            var type = configuration.GetType();
            var description = type.GetCustomAttribute<ConfigDescriptionAttribute>();
            var properties = new List<ConfigProperty>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyDescription = property.GetCustomAttribute<ConfigDescriptionAttribute>();
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
                {
                    logger.LogWarning("配置 {ConfigId} 的字段 {PropertyName} 不可读写，未暴露给 WebUI。", id, property.Name);
                    continue;
                }
                if (!ConfigProperty.TryCreate(
                    property,
                    propertyDescription?.Name ?? property.Name,
                    propertyDescription?.Description ?? string.Empty,
                    out var configProperty))
                {
                    logger.LogWarning("配置 {ConfigId} 的字段 {PropertyName} 类型 {PropertyType} 不受支持，未暴露给 WebUI。", id, property.Name, property.PropertyType.Name);
                    continue;
                }
                properties.Add(configProperty);
            }
            return new RegisteredConfig(
                id,
                configuration,
                onSave,
                description?.Name ?? type.Name,
                description?.Description ?? string.Empty,
                properties);
        }

        public ConfigSectionDto ToDto()
            => new(
                Id,
                Name,
                Description,
                properties.Select(property => property.ToDto(configuration)).ToList());

        public async Task SaveAsync(IReadOnlyDictionary<string, JsonElement> fields)
        {
            ArgumentNullException.ThrowIfNull(fields);
            await saveLock.WaitAsync();
            try
            {
                var changedProperties = new Dictionary<ConfigProperty, object?>();
                foreach (var (key, value) in fields)
                {
                    if (!propertiesByKey.TryGetValue(key, out var property))
                    {
                        throw new ArgumentException($"配置字段不存在或不可编辑: {key}", nameof(fields));
                    }
                    changedProperties[property] = property.Convert(value);
                }

                // 在开始写入实例前完成全部类型转换，避免错误请求留下半更新状态。
                var originalValues = changedProperties.Keys.ToDictionary(
                    property => property,
                    property => property.CloneValue(property.Property.GetValue(configuration)));
                foreach (var (property, value) in changedProperties)
                {
                    property.Property.SetValue(configuration, value);
                }

                try
                {
                    await onSave();
                }
                catch
                {
                    foreach (var (property, value) in originalValues)
                    {
                        property.Property.SetValue(configuration, value);
                    }
                    throw;
                }
            }
            finally
            {
                saveLock.Release();
            }
        }
    }

    private sealed class ConfigProperty
    {
        private ConfigProperty(
            PropertyInfo property,
            string name,
            string description,
            ConfigFieldKind kind,
            Type valueType,
            Type? listElementType)
        {
            Property = property;
            Key = property.Name;
            Name = name;
            Description = description;
            Kind = kind;
            ValueType = valueType;
            ListElementType = listElementType;
        }

        public PropertyInfo Property { get; }
        public string Key { get; }
        public string Name { get; }
        public string Description { get; }
        public ConfigFieldKind Kind { get; }
        public Type ValueType { get; }
        public Type? ListElementType { get; }

        public static bool TryCreate(PropertyInfo property, string name, string description, out ConfigProperty result)
        {
            var declaredType = property.PropertyType;
            var valueType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (TryGetScalarKind(valueType, out var kind))
            {
                result = new ConfigProperty(property, name, description, kind, valueType, null);
                return true;
            }

            if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = declaredType.GetGenericArguments()[0];
                var underlyingElementType = Nullable.GetUnderlyingType(elementType) ?? elementType;
                if (underlyingElementType == typeof(string))
                {
                    // 字符串列表在 WebUI 中渲染为下拉 + 添加/删除控件
                    result = new ConfigProperty(property, name, description, ConfigFieldKind.StringList, declaredType, elementType);
                    return true;
                }
                if (TryGetScalarKind(underlyingElementType, out _))
                {
                    result = new ConfigProperty(property, name, description, ConfigFieldKind.List, declaredType, elementType);
                    return true;
                }
            }

            result = null!;
            return false;
        }

        public ConfigFieldDto ToDto(object instance)
        {
            var value = Property.GetValue(instance);
            var enumOptions = Kind == ConfigFieldKind.Enum
                ? Enum.GetNames(ValueType)
                : null;
            return new ConfigFieldDto(Key, Name, Description, Kind.ToString().ToLowerInvariant(), ToApiValue(value), enumOptions);
        }

        public object? Convert(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                if (!Property.PropertyType.IsValueType || Nullable.GetUnderlyingType(Property.PropertyType) != null)
                {
                    return null;
                }
                throw new ArgumentException($"字段 {Name} 不允许为空。");
            }

            return Kind is ConfigFieldKind.List or ConfigFieldKind.StringList
                ? ConvertList(element)
                : ConvertScalar(element, ValueType, Kind);
        }

        public object? CloneValue(object? value)
        {
            if (value is not IList list || Kind is not (ConfigFieldKind.List or ConfigFieldKind.StringList))
            {
                return value;
            }

            var clone = (IList)Activator.CreateInstance(ValueType)!;
            foreach (var item in list)
            {
                clone.Add(item);
            }
            return clone;
        }

        private object? ToApiValue(object? value)
        {
            if (value is null)
            {
                return null;
            }
            if (Kind == ConfigFieldKind.Enum)
            {
                return value.ToString();
            }
            if (Kind is not (ConfigFieldKind.List or ConfigFieldKind.StringList) || value is not IEnumerable values)
            {
                return value;
            }

            var elementType = Nullable.GetUnderlyingType(ListElementType!) ?? ListElementType!;
            return values.Cast<object?>()
                .Select(item => elementType.IsEnum ? item?.ToString() : item)
                .ToList();
        }

        private object ConvertList(JsonElement element)
        {
            using var parsedDocument = element.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(element.GetString() ?? throw new ArgumentException($"字段 {Name} 的列表内容不能为空。"))
                : null;
            var array = parsedDocument?.RootElement ?? element;
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException($"字段 {Name} 必须是 JSON 数组。");
            }

            var elementType = Nullable.GetUnderlyingType(ListElementType!) ?? ListElementType!;
            if (!TryGetScalarKind(elementType, out var elementKind))
            {
                throw new ArgumentException($"字段 {Name} 的列表元素类型不受支持。");
            }
            var list = (IList)Activator.CreateInstance(ValueType)!;
            foreach (var item in array.EnumerateArray())
            {
                list.Add(ConvertScalar(item, elementType, elementKind));
            }
            return list;
        }

        private static object ConvertScalar(JsonElement element, Type type, ConfigFieldKind kind)
        {
            if (kind == ConfigFieldKind.String)
            {
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : throw new ArgumentException("字符串字段必须使用字符串值。");
            }
            if (kind == ConfigFieldKind.Boolean)
            {
                if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
                {
                    return element.GetBoolean();
                }
                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed))
                {
                    return parsed;
                }
                throw new ArgumentException("布尔字段必须是 true 或 false。");
            }
            if (kind == ConfigFieldKind.Enum)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(type, value, true, out var parsed))
                    {
                        return parsed;
                    }
                }
                else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
                {
                    var parsed = Enum.ToObject(type, numeric);
                    if (Enum.IsDefined(type, parsed))
                    {
                        return parsed;
                    }
                }
                throw new ArgumentException($"枚举字段必须是 {string.Join("、", Enum.GetNames(type))} 之一。");
            }

            var text = element.ValueKind switch
            {
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.String => element.GetString(),
                _ => throw new ArgumentException("数值字段必须是数字。"),
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("数值字段不能为空。\n");
            }
            return type == typeof(byte) ? byte.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(sbyte) ? sbyte.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(short) ? short.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(ushort) ? ushort.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(int) ? int.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(uint) ? uint.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(long) ? long.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(ulong) ? ulong.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : type == typeof(float) ? float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : type == typeof(double) ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : type == typeof(decimal) ? decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
                : throw new ArgumentException($"不支持的数值类型: {type.Name}");
        }

        private static bool TryGetScalarKind(Type type, out ConfigFieldKind kind)
        {
            if (type == typeof(string))
            {
                kind = ConfigFieldKind.String;
                return true;
            }
            if (type == typeof(bool))
            {
                kind = ConfigFieldKind.Boolean;
                return true;
            }
            if (type.IsEnum)
            {
                kind = ConfigFieldKind.Enum;
                return true;
            }
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                kind = ConfigFieldKind.Number;
                return true;
            }
            kind = default;
            return false;
        }
    }

    private enum ConfigFieldKind
    {
        String,
        Boolean,
        Number,
        Enum,
        List,
        StringList,
    }
}
