using System.Text.Json.Serialization;

namespace Agent.Tools;

/// <summary>
/// Agent.Tools 工具参数类型的 STJ source generator 上下文（NativeAOT 兼容）。
/// 注册所有 AddFunction&lt;T&gt; 的参数类型,供 ToolSetBridge.Builder 反序列化工具入参。
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TimeToolSet.CurrentTimeArgs))]
[JsonSerializable(typeof(SkillToolSet.SkillListArgs))]
[JsonSerializable(typeof(SkillToolSet.SkillReadArgs))]
[JsonSerializable(typeof(SubAgentToolSet.SubagentArgs))]
[JsonSerializable(typeof(SubAgentToolSet.SubagentOutputArgs))]
[JsonSerializable(typeof(SubAgentToolSet.SubagentStopArgs))]
[JsonSerializable(typeof(TodoListToolSet.TodoListArgs))]
[JsonSerializable(typeof(WebTools.WebSearchArgs))]
[JsonSerializable(typeof(WebTools.WebFetchArgs))]
internal sealed partial class AgentToolsJsonContext : JsonSerializerContext
{
}