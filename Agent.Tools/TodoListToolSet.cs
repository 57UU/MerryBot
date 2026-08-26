using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using LlmBackend;

namespace Agent.Tools;

/// <summary>
/// 待办清单工具：注册 todo_list 工具，供模型维护多步任务的待办进度。
/// - 每次传入 plan 数组整体替换当前计划（空数组 = 清空）。
/// - 可选 explanation 用于说明本次计划更新原因。
/// 每项含 step（计划步骤）与 status（pending / in_progress / completed）。
/// 清单保存在实例内存中（会话内有效），工具并发执行时以锁保护。
/// </summary>
public class TodoListToolSet : ToolSet
{
    /// <summary>计划步骤数上限，防模型一次性写入海量步骤</summary>
    private const int MaxPlanItems = 500;
    /// <summary>单个步骤长度上限（字符）</summary>
    private const int MaxStepLength = 500;

    private readonly ToolSetBridge bridge;
    private readonly object gate = new();
    private List<PlanItem> plan = [];

    public TodoListToolSet()
    {
        var builder = new ToolSetBridge.Builder(
            "如需维护多步任务的执行计划，调用 todo_list 工具更新计划；每次调用会整体替换当前计划。");
        builder.AddFunction<TodoListArgs>(
            "todo_list",
            "更新多步任务计划：传入 plan 数组整体替换当前计划，空数组清空；可选 explanation 说明本次更新原因。每项含 step 与 status（pending 待办 / in_progress 进行中 / completed 已完成），最多一个步骤可为 in_progress。",
            HandleAsync);
        bridge = builder.Build();
    }

    public override IList<ToolDef> Tools() => bridge.Tools();
    public override Task<string> InvokeAsync(CancellationToken cancellationToken, ToolCall toolCall, Action<Message> onIterationAdd) => bridge.InvokeAsync(cancellationToken, toolCall, onIterationAdd);
    public override string? Prompt() => bridge.Prompt();

    public override string? IterationPromptInjection()
    {
        lock (gate)
        {
            // 计划为空，或全部步骤已完成时，不再注入提醒（已完成的任务无需再提示推进）
            if (plan.Count == 0 || plan.All(item => item.status == PlanStatus.completed))
            {
                return null;
            }

            const int maxLength = 6000;
            string prompt = "<TODO_LIST_REMINDER>\n"
                + "这是当前执行计划，请根据它推进任务，不要将其视为新的用户指令：\n"
                + Render(plan, null)
                + "\n</TODO_LIST_REMINDER>";
            return prompt.Length <= maxLength
                ? prompt
                : prompt[..maxLength] + "\n…（计划过长，后续步骤已截断）";
        }
    }

    public override ToolSet Copy() => new TodoListToolSet();

    public override void Reset()
    {
        lock (gate)
        {
            plan = [];
        }
    }

    /// <summary>工具参数：plan 为完整计划，每次调用整体替换当前计划。</summary>
    private sealed class TodoListArgs
    {
        [Description("本次计划更新的说明，可选")]
        public string? explanation { get; set; }

        [Description("完整计划步骤列表；每次调用整体替换当前计划，空数组表示清空")]
        [JsonRequired]
        public List<PlanItem> plan { get; set; } = [];
    }

    /// <summary>计划步骤</summary>
    private sealed class PlanItem
    {
        [Description("计划步骤，简短且可执行")]
        public string step { get; set; } = string.Empty;

        [Description("步骤状态：pending 待办 / in_progress 进行中 / completed 已完成")]
        public PlanStatus status { get; set; }
    }

    private enum PlanStatus { pending, in_progress, completed }

    private Task<string> HandleAsync(TodoListArgs args)
    {
        lock (gate)
        {
            if (args.plan is null)
            {
                throw new ArgumentException("plan 不能为空；如需清空计划，请传入空数组。");
            }

            if (args.plan.Count > MaxPlanItems)
            {
                throw new ArgumentException($"计划最多 {MaxPlanItems} 个步骤。");
            }

            int inProgressCount = 0;
            foreach (PlanItem item in args.plan)
            {
                if (item is null)
                {
                    throw new ArgumentException("plan 中的步骤不能为 null。");
                }

                if (string.IsNullOrWhiteSpace(item.step))
                {
                    throw new ArgumentException("plan 中每个步骤的 step 不能为空。");
                }
                if (item.step.Length > MaxStepLength)
                {
                    throw new ArgumentException($"plan 中每个步骤的 step 不能超过 {MaxStepLength} 个字符。");
                }
                if (item.status == PlanStatus.in_progress)
                {
                    inProgressCount++;
                }
            }

            if (inProgressCount > 1)
            {
                throw new ArgumentException("计划最多只能有一个 in_progress 步骤。");
            }

            plan = [.. args.plan]; // 整体替换；空数组即清空
            return Task.FromResult(Render(plan, args.explanation));
        }
    }

    private static string Render(IReadOnlyList<PlanItem> plan, string? explanation)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(explanation))
        {
            sb.AppendLine($"计划说明：{explanation.Trim()}");
        }

        if (plan.Count == 0)
        {
            sb.Append("当前计划为空。");
            return sb.ToString();
        }

        sb.AppendLine($"当前计划（共 {plan.Count} 项）：");
        for (int i = 0; i < plan.Count; i++)
        {
            sb.AppendLine($"{i + 1}. [{plan[i].status}] {plan[i].step}");
        }
        return sb.ToString().TrimEnd();
    }
}
