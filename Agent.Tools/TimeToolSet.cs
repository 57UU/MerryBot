using LlmBackend;

namespace Agent.Tools;

/// <summary>Provides the current local and UTC time without external dependencies.</summary>
public sealed class TimeToolSet : ToolSet
{
    private readonly ToolSetBridge bridge;
    private readonly TimeProvider timeProvider;

    public TimeToolSet(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var builder = new ToolSetBridge.Builder();
        builder.AddFunction<CurrentTimeArgs>(
            "current_time",
            "获取当前本地时间、时区和 UTC 时间。",
            AgentToolsJsonContext.Default.CurrentTimeArgs,
            GetCurrentTimeAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();

    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) =>
        bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);

    public override string? Prompt() => bridge.Prompt();

    private Task<string> GetCurrentTimeAsync(CurrentTimeArgs _)
    {
        var localNow = timeProvider.GetLocalNow();
        return Task.FromResult(
            $"当前本地时间: {localNow:O}\n" +
            $"时区: {TimeZoneInfo.Local.Id}\n" +
            $"UTC 时间: {localNow.UtcDateTime:O}");
    }

    internal sealed class CurrentTimeArgs
    {
    }
}
