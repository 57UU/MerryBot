using System.Text.Json.Serialization;

namespace Agent.Session;

/// <summary>
/// Agent.Session 的 STJ source generator 上下文（NativeAOT 兼容）。
/// 注册 Cron 工具参数类型与定时任务系列化模型。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClockTask))]
[JsonSerializable(typeof(ClockTaskSummary))]
[JsonSerializable(typeof(List<ClockTaskSummary>))]
[JsonSerializable(typeof(ClockDeleteResult))]
[JsonSerializable(typeof(ClockRunLog))]
[JsonSerializable(typeof(List<ClockRunLog>))]
[JsonSerializable(typeof(Cron.ClockCreateArgs))]
[JsonSerializable(typeof(Cron.ClockListArgs))]
[JsonSerializable(typeof(Cron.ClockGetArgs))]
[JsonSerializable(typeof(Cron.ClockUpdateArgs))]
[JsonSerializable(typeof(Cron.ClockDeleteArgs))]
[JsonSerializable(typeof(Cron.ClockLogArgs))]
[JsonSerializable(typeof(TerminalToolSet.BashArgs))]
[JsonSerializable(typeof(TerminalToolSet.LoadLocalImageArgs))]
[JsonSerializable(typeof(TerminalToolSet.TaskListArgs))]
[JsonSerializable(typeof(TerminalToolSet.TaskOutputArgs))]
[JsonSerializable(typeof(TerminalToolSet.TaskStopArgs))]
internal sealed partial class AgentSessionJsonContext : JsonSerializerContext
{
}