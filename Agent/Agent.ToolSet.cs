using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmBackend;

namespace Agent;

/// <summary>
/// Low Level Tool Set。抽象基类：子类实现具体工具，通用成员（如 OnIterationAdd 回调）由基类提供
/// </summary>
public abstract class ToolSet
{
    public abstract IList<ToolDef> Tools();
    public abstract Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall);
    public abstract string? Prompt();

    /// <summary>
    /// 工具在调用期间向当前对话迭代追加内容（例如把图片以用户消息加入）的回调，
    /// 由 Agent 在工具执行期间注入，执行结束后恢复为 null
    /// </summary>
    public Action<Message>? OnIterationAdd { get; set; }
}

/// <summary>
/// 将 C# 函数自动注册为 LLM 工具集。通过反射从参数类型 T 的公开属性生成 JSON Schema
/// 参数列表（类型映射、说明、枚举取值、嵌套对象、数组、必填项），调用时把模型返回的
/// JSON 参数反序列化为 T 并执行对应函数，函数返回值即工具调用结果。
/// </summary>
public class ToolSetBridge : ToolSet
{
    private readonly string? prompt;
    private readonly List<RegisteredTool> tools;

    private ToolSetBridge(string? prompt, List<RegisteredTool> tools)
    {
        this.prompt = prompt;
        this.tools = tools;
    }

    private sealed class RegisteredTool
    {
        public required ToolDef Def { get; init; }
        public required Func<JsonElement, Task<string>> Invoker { get; init; }
    }

    public override IList<ToolDef> Tools()
    {
        return tools.Select(t => t.Def).ToList();
    }

    public override async Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = tools.FirstOrDefault(t => t.Def.function.name == toolCall.Name)
            ?? throw new KeyNotFoundException($"工具未注册: {toolCall.Name}");
        try
        {
            var args = string.IsNullOrWhiteSpace(toolCall.Arguments) ? "{}" : toolCall.Arguments;
            using var doc = JsonDocument.Parse(args);
            return await tool.Invoker(doc.RootElement);
        }
        catch (Exception e)
        {
            // 参数解析或函数执行失败时返回错误信息，便于模型自行纠正后重试
            return $"{{\"error\": {JsonSerializer.Serialize(e.Message)}}}";
        }
    }

    public override string? Prompt()
    {
        return prompt;
    }

    public class Builder
    {
        // net9.0 起默认 JsonSerializer 不再将字符串解析为枚举，而 Schema 以字符串 enum 值描述枚举，
        // 故显式注册 JsonStringEnumConverter 保持一致
        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string? prompt;
        private readonly List<RegisteredTool> tools = new();
        
        public Builder(string? prompt=null)
        {
            this.prompt = prompt;
        }

        /// <summary>
        /// 注册一个 C# 函数为 LLM 工具。参数类型 T 的属性会通过反射自动生成 JSON Schema，
        /// 支持 DescriptionAttribute（参数说明）、JsonPropertyNameAttribute（JSON 字段名）、
        /// JsonRequiredAttribute（强制必填）、JsonIgnoreAttribute（跳过该属性）；
        /// Nullable 与可空引用类型属性自动视为可选参数。函数为异步签名，返回 Task&lt;string&gt;。
        /// </summary>
        public Builder AddFunction<T>(string name, string description, Func<T, Task<string>> function)
        {
            var def = new ToolDef
            {
                type = "function",
                function = new FunctionDef
                {
                    name = name,
                    description = description,
                    parameters = JsonSerializer.SerializeToElement(BuildTypeSchema(typeof(T), new HashSet<Type>())),
                },
            };
            tools.Add(new RegisteredTool
            {
                Def = def,
                Invoker = json => function(json.Deserialize<T>(DeserializeOptions)!),
            });
            return this;
        }

        public ToolSetBridge Build()
        {
            return new ToolSetBridge(prompt, tools);
        }

        /// <summary>
        /// 递归生成 JSON Schema：基础类型映射为 string/integer/number/boolean，
        /// 枚举带 enum 取值，集合带 items，复杂对象递归展开 properties。
        /// </summary>
        private static Dictionary<string, object?> BuildTypeSchema(Type type, HashSet<Type> visited)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) type = underlying;

            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) ||
                type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return new() { ["type"] = "string" };
            if (type == typeof(bool))
                return new() { ["type"] = "boolean" };
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong))
                return new() { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new() { ["type"] = "number" };
            if (type.IsEnum)
                return new() { ["type"] = "string", ["enum"] = Enum.GetNames(type).ToList() };

            // 字典 / JsonElement / object 一律按自由对象处理
            if (type == typeof(object) || type == typeof(JsonElement) || typeof(IDictionary).IsAssignableFrom(type))
                return new() { ["type"] = "object" };

            var elementType = GetEnumerableElementType(type);
            if (elementType != null)
                return new() { ["type"] = "array", ["items"] = BuildTypeSchema(elementType, visited) };

            // 复杂对象：展开属性；visited 防止自引用类型无限递归
            if (!visited.Add(type))
                return new() { ["type"] = "object" };

            var properties = new Dictionary<string, object?>();
            var required = new List<string>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.GetMethod == null) continue;
                if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                var propName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
                var propSchema = BuildTypeSchema(prop.PropertyType, visited);
                var desc = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (!string.IsNullOrEmpty(desc)) propSchema["description"] = desc;
                if (IsPropertyRequired(prop)) required.Add(propName);
                properties[propName] = propSchema;
            }

            var schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        private static Type? GetEnumerableElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (!type.IsGenericType) return null;
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>) || def == typeof(IEnumerable<>))
                return type.GetGenericArguments()[0];
            return null;
        }

        /// <summary>
        /// 判定属性是否为必填：Nullable&lt;T&gt;（int?、string? 等）视为可选；
        /// 非空值类型（int、bool、枚举）必填；引用类型按 NRT 可空性判定（string 必填、string? 可选）；
        /// [JsonRequired] 强制必填。
        /// </summary>
        private static readonly NullabilityInfoContext Nullability = new();

        private static bool IsPropertyRequired(PropertyInfo prop)
        {
            if (prop.GetCustomAttribute<JsonRequiredAttribute>() != null) return true;
            var type = prop.PropertyType;
            if (Nullable.GetUnderlyingType(type) != null) return false; // Nullable<T> → 可选
            if (type.IsValueType) return true;                           // 非空值类型 → 必填
            return Nullability.Create(prop).WriteState == NullabilityState.NotNull; // 引用类型按 NRT 判定
        }
    }
}
