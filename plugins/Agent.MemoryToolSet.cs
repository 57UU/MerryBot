using Agent;
using CommonLib;
using LlmBackend;
using System.ComponentModel;
using System.Text;

namespace BotPlugin;

/// <summary>为一个 Session 提供持久记忆工具；存储实现仅依赖 <see cref="IMemoryManagementService"/>。</summary>
public sealed class MemoryToolSet : ToolSet
{
    private readonly IMemoryManagementService memoryService;
    private readonly string sessionKey;
    private readonly ToolSetBridge bridge;

    public MemoryToolSet(IMemoryManagementService memoryService, string sessionKey, string? promptInjection)
    {
        this.memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        this.sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        _ = SessionKey.Parse(sessionKey);

        var builder = new ToolSetBridge.Builder(BuildPrompt(promptInjection));
        builder.AddFunction<SaveMemoryArgs>("save_memory", "保存或更新一条持久记忆。适合记录用户偏好、重要事实或项目进度。", SaveMemoryAsync);
        builder.AddFunction<RecallMemoryArgs>("recall_memory", "列出当前会话的所有记忆 key；使用 query_memory 读取指定记忆。", RecallMemoryAsync);
        builder.AddFunction<QueryMemoryArgs>("query_memory", "读取指定 key 的持久记忆内容。", QueryMemoryAsync);
        builder.AddFunction<DeleteMemoryArgs>("delete_memory", "删除指定 key 的持久记忆。", DeleteMemoryAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    private static string BuildPrompt(string? promptInjection)
    {
        var builder = new StringBuilder("你拥有当前会话独立的持久记忆。遇到值得在后续对话中保留的用户偏好、重要事实或进度时，使用 save_memory 保存；需要具体内容时，使用 query_memory 按 key 读取。index 中的内容仅供参考，不可修改。");
        if (!string.IsNullOrWhiteSpace(promptInjection))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(promptInjection);
        }
        return builder.ToString();
    }

    private async Task<string> SaveMemoryAsync(SaveMemoryArgs args)
    {
        await memoryService.SaveMemoryAsync(sessionKey, args.key, args.content);
        return $"已记忆: {args.key}";
    }

    private async Task<string> RecallMemoryAsync(RecallMemoryArgs _)
    {
        var entries = await memoryService.ListMemoriesAsync(sessionKey);
        return entries.Count > 0 ? string.Join('\n', entries.Select(entry => entry.Key)) : "当前没有记忆。";
    }

    private async Task<string> QueryMemoryAsync(QueryMemoryArgs args)
    {
        var entry = await memoryService.GetMemoryAsync(sessionKey, args.key);
        return entry?.Content ?? $"未找到记忆: {args.key}";
    }

    private async Task<string> DeleteMemoryAsync(DeleteMemoryArgs args)
    {
        return await memoryService.DeleteMemoryAsync(sessionKey, args.key)
            ? $"已删除记忆: {args.key}"
            : $"未找到记忆: {args.key}";
    }

    private sealed class SaveMemoryArgs
    {
        [Description("记忆的简短标识，例如“用户偏好”或“项目进度”；不可为 index")]
        public string key { get; set; } = string.Empty;
        [Description("要保存的记忆内容，可使用 Markdown")]
        public string content { get; set; } = string.Empty;
    }

    private sealed class RecallMemoryArgs { }
    private sealed class QueryMemoryArgs { [Description("要读取的记忆 key")] public string key { get; set; } = string.Empty; }
    private sealed class DeleteMemoryArgs
    {
        [Description("要删除的记忆 key")]
        public string key { get; set; } = string.Empty;
    }
}
