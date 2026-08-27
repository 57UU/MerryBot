using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmBackend;

namespace Agent;

/// <summary>
/// Low Level Tool Set。工具调用期间产生的附加消息通过 <paramref name="onIterationAdd"/> 回传给 Agent。
/// </summary>
public abstract class ToolSet
{
    public abstract IList<ToolDef> Tools();
    public abstract Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd);
    public abstract string? Prompt();

    /// <summary>
    /// 返回追加到当前用户输入前的动态提示。默认不注入任何内容；
    /// 有会话状态的 ToolSet 可覆盖此方法提供当前状态快照。
    /// </summary>
    public virtual string? IterationPromptInjection() => null;

    /// <summary>
    /// 为新的 Agent 复制 ToolSet。默认复用无状态实例；持有可变会话状态的 ToolSet
    /// 应覆盖此方法，返回状态隔离的新实例。
    /// </summary>
    public virtual ToolSet Copy() => this;

    /// <summary>
    /// 清理 ToolSet 的会话级状态。默认无操作，供 Agent.ResetAsync 调用。
    /// </summary>
    public virtual void Reset()
    {
    }
}
/// <summary>
/// 提供系统提示的工具集，不包含任何工具。
/// </summary>
public class PromptToolSet : ToolSet
{
    public PromptToolSet(string prompt)
    {
        this.prompt = prompt;
    }

    private readonly string prompt;

    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
    {
        //this should never be called
        throw new NotImplementedException();
    }

    public override string? Prompt() => prompt;

    public override IList<ToolDef> Tools()
    {
        return [];
    }
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
    private readonly IList<ToolDef> toolDefs;

    private ToolSetBridge(string? prompt, List<RegisteredTool> tools)
    {
        this.prompt = prompt;
        this.tools = tools;
        // 工具定义在构造后不可变，缓存避免每次调用全量重建/遍历（Agent 每轮都会扫 Tools()）
        this.toolDefs = tools.Select(t => t.Def).ToList();
    }

    private sealed class RegisteredTool
    {
        public required ToolDef Def { get; init; }
        public required Func<JsonElement, CancellationToken, Action<Message>, Task<string>> Invoker { get; init; }
    }

    public override IList<ToolDef> Tools()
    {
        return toolDefs;
    }

    public override async Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = tools.FirstOrDefault(t => t.Def.function.name == toolCall.Name)
            ?? throw new KeyNotFoundException($"工具未注册: {toolCall.Name}");
        try
        {
            var args = string.IsNullOrWhiteSpace(toolCall.Arguments) ? "{}" : toolCall.Arguments;
            using var doc = JsonDocument.Parse(args);
            return await tool.Invoker(doc.RootElement, cancellationToken, onIterationAdd);
        }
        catch (OperationCanceledException)
        {
            // 取消（会话取消或工具超时）不是工具错误：原样传播，由 Agent 统一回填取消结果
            throw;
        }
        // 非取消类异常不再在此吞掉并伪装成成功结果：交由上层 InvokeToolAsync 统一捕获，
        // 回填相同的 {"error":...} JSON（模型仍可自纠重试），同时记录 ToolCallFailed 状态，
        // 避免 TUI 将工具失败误显为"已完成"。
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

        public Builder(string? prompt = null)
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
            => AddFunctionCore<T>(name, description, (json, _, _) => function(json.Deserialize<T>(DeserializeOptions)!));

        /// <summary>
        /// 注册需要在本次工具调用期间向 Agent 追加消息的函数。第二个参数由调用方注入；
        /// 与单参数函数并存，普通工具无需感知该回调。
        /// </summary>
        public Builder AddFunction<T>(string name, string description, Func<T, Action<Message>, Task<string>> function)
            => AddFunctionCore<T>(name, description, (json, _, onIterationAdd) => function(json.Deserialize<T>(DeserializeOptions)!, onIterationAdd));

        /// <summary>
        /// 注册需要感知取消的工具函数（如网络下载）。第三个参数为本次调用携带的
        /// CancellationToken，与 Agent 的 per-tool 超时/会话取消联动；与无 token 的重载并存。
        /// </summary>
        public Builder AddFunction<T>(string name, string description, Func<T, CancellationToken, Action<Message>, Task<string>> function)
            => AddFunctionCore<T>(name, description, (json, cancellationToken, onIterationAdd) => function(json.Deserialize<T>(DeserializeOptions)!, cancellationToken, onIterationAdd));

        private Builder AddFunctionCore<T>(string name, string description, Func<JsonElement, CancellationToken, Action<Message>, Task<string>> invoker)
        {
            var def = new ToolDef
            {
                type = "function",
                function = new FunctionDef
                {
                    name = name,
                    description = description,
                    parameters = JsonSerializer.SerializeToElement(BuildTypeSchema(typeof(T), new List<Type>())),
                },
            };
            tools.Add(new RegisteredTool
            {
                Def = def,
                Invoker = invoker,
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
        private static Dictionary<string, object?> BuildTypeSchema(Type type, List<Type> path)
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
                return new() { ["type"] = "array", ["items"] = BuildTypeSchema(elementType, path) };

            // 复杂对象：展开属性；仅当类型出现在当前展开路径上（循环/自引用）时截断为空 schema，
            // 兄弟属性出现同一类型时仍完整展开（visited 全局去重会把第二次出现降级为空 schema）
            if (path.Contains(type))
                return new() { ["type"] = "object" };

            path.Add(type);
            try
            {
                var properties = new Dictionary<string, object?>();
                var required = new List<string>();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0 || prop.GetMethod == null) continue;
                    if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                    var propName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
                    var propSchema = BuildTypeSchema(prop.PropertyType, path);
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
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
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
        /// <summary>NullabilityInfoContext 非线程安全（内部缓存字典），并发构建 ToolSet（如会话并发创建）时须串行化</summary>
        private static readonly object NullabilityLock = new();

        private static bool IsPropertyRequired(PropertyInfo prop)
        {
            if (prop.GetCustomAttribute<JsonRequiredAttribute>() != null) return true;
            var type = prop.PropertyType;
            if (Nullable.GetUnderlyingType(type) != null) return false; // Nullable<T> → 可选
            if (type.IsValueType) return true;                           // 非空值类型 → 必填
            lock (NullabilityLock)
            {
                return Nullability.Create(prop).WriteState == NullabilityState.NotNull; // 引用类型按 NRT 判定
            }
        }
    }
}
