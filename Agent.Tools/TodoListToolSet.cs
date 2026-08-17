using System.ComponentModel;
using System.Text;
using LlmBackend;

namespace Agent.Tools;

/// <summary>
/// 待办清单工具：注册 todo_list 工具，供模型维护多步任务的待办进度。
/// - 不带 todos 参数：查看当前清单。
/// - 传 todos 数组：整体替换清单（空数组 = 清空）。
/// 每项含 title（任务标题）与 status（pending / in_progress / done）。
/// 清单保存在实例内存中（会话内有效），工具并发执行时以锁保护。
/// </summary>
public class TodoListToolSet : ToolSet
{
    /// <summary>清单条目数上限，防模型一次性写入海量条目</summary>
    private const int MaxTodoItems = 500;
    /// <summary>单条标题长度上限（字符）</summary>
    private const int MaxTitleLength = 500;

    private readonly ToolSetBridge bridge;
    private readonly object gate = new();
    private List<TodoItem> items = [];

    public TodoListToolSet()
    {
        var builder = new ToolSetBridge.Builder(
            "如需维护多步任务的待办进度，调用 todo_list 工具查看或更新清单");
        builder.AddFunction<TodoListArgs>(
            "todo_list",
            "查看或更新待办清单：不带 todos 参数查看当前清单；传 todos 数组则整体替换（空数组清空）。每项含 title 与 status（pending 待办 / in_progress 进行中 / done 已完成）。",
            AgentToolsJsonContext.Default.TodoListArgs,
            HandleAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    /// <summary>工具参数：todos 缺省表示仅查询</summary>
    internal sealed class TodoListArgs
    {
        public List<TodoItem>? todos { get; set; }
    }

    /// <summary>清单项</summary>
    internal sealed class TodoItem
    {
        [Description("任务标题，简短可执行")]
        public string title { get; set; } = string.Empty;

        [Description("任务状态：pending 待办 / in_progress 进行中 / done 已完成")]
        public TodoStatus status { get; set; }
    }

    internal enum TodoStatus { pending, in_progress, done }

    private Task<string> HandleAsync(TodoListArgs args)
    {
        lock (gate)
        {
            if (args.todos != null)
            {
                if (args.todos.Count > MaxTodoItems)
                {
                    throw new ArgumentException($"待办清单最多 {MaxTodoItems} 项。");
                }
                foreach (var item in args.todos)
                {
                    if (string.IsNullOrWhiteSpace(item.title))
                    {
                        throw new ArgumentException("todos 中每项的 title 不能为空");
                    }
                    if (item.title.Length > MaxTitleLength)
                    {
                        throw new ArgumentException($"todos 中每项的 title 不能超过 {MaxTitleLength} 字符");
                    }
                }
                items = args.todos; // 整体替换；空数组即清空
            }
            return Task.FromResult(Render(items));
        }
    }

    private static string Render(List<TodoItem> items)
    {
        if (items.Count == 0) return "待办清单为空。";

        var sb = new StringBuilder();
        sb.AppendLine($"当前待办清单（共 {items.Count} 项）：");
        for (int i = 0; i < items.Count; i++)
        {
            sb.AppendLine($"{i + 1}. [{items[i].status}] {items[i].title}");
        }
        return sb.ToString().TrimEnd();
    }
}
