using System.Text.Json;
using Agent.Tools;
using LlmBackend;

namespace MerryBot.Test;

/// <summary>
/// 验证 todo_list 与内部计划工具的接口和行为保持一致：
/// plan/explanation 参数、step/status 字段、整体替换以及单个进行中步骤约束。
/// </summary>
public sealed class TodoListToolSetTests
{
    [Fact]
    public void ToolSchema_UsesPlanShape()
    {
        TodoListToolSet toolSet = new();
        ToolDef definition = Assert.Single(toolSet.Tools());

        Assert.Equal("todo_list", definition.function.name);
        Assert.True(definition.function.parameters.HasValue);

        JsonElement schema = definition.function.parameters.Value;
        JsonElement properties = schema.GetProperty("properties");
        JsonElement plan = properties.GetProperty("plan");
        JsonElement itemProperties = plan.GetProperty("items").GetProperty("properties");
        JsonElement status = itemProperties.GetProperty("status");

        Assert.Equal("array", plan.GetProperty("type").GetString());
        Assert.Equal("string", itemProperties.GetProperty("step").GetProperty("type").GetString());
        Assert.Equal("string", status.GetProperty("type").GetString());
        Assert.Equal(
            ["pending", "in_progress", "completed"],
            status.GetProperty("enum").EnumerateArray().Select(static value => value.GetString()).ToArray());
        Assert.True(HasRequiredProperty(schema, "plan"));
        Assert.False(HasRequiredProperty(schema, "explanation"));
    }

    [Fact]
    public async Task Update_ReplacesPlanAndRendersExplanation()
    {
        TodoListToolSet toolSet = new();

        string first = await InvokeAsync(
            toolSet,
            """{"explanation":"开始处理","plan":[{"step":"检查代码","status":"in_progress"}]}""");
        string second = await InvokeAsync(
            toolSet,
            """{"explanation":"检查已完成","plan":[{"step":"检查代码","status":"completed"},{"step":"运行测试","status":"pending"}]}""");

        Assert.Contains("计划说明：开始处理", first);
        Assert.Contains("[in_progress] 检查代码", first);
        Assert.Contains("计划说明：检查已完成", second);
        Assert.Contains("[completed] 检查代码", second);
        Assert.Contains("[pending] 运行测试", second);
        Assert.DoesNotContain("开始处理", second);
    }

    [Fact]
    public async Task EmptyPlan_ClearsPlan()
    {
        TodoListToolSet toolSet = new();

        await InvokeAsync(toolSet, """{"plan":[{"step":"临时步骤","status":"pending"}]}""");
        string result = await InvokeAsync(toolSet, """{"explanation":"已清空","plan":[]}""");

        Assert.Contains("计划说明：已清空", result);
        Assert.Contains("当前计划为空。", result);
        Assert.DoesNotContain("临时步骤", result);
    }

    [Fact]
    public async Task IterationPrompt_CopyAndReset_Isolated()
    {
        TodoListToolSet toolSet = new();
        await InvokeAsync(toolSet, """{"plan":[{"step":"主任务","status":"in_progress"}]}""");

        string reminder = toolSet.IterationPromptInjection()!;
        Assert.StartsWith("<TODO_LIST_REMINDER>", reminder);
        Assert.Contains("[in_progress] 主任务", reminder);

        TodoListToolSet copy = (TodoListToolSet)toolSet.Copy();
        Assert.NotSame(toolSet, copy);
        Assert.Null(copy.IterationPromptInjection());

        await InvokeAsync(copy, """{"plan":[{"step":"子任务","status":"pending"}]}""");
        Assert.Contains("主任务", toolSet.IterationPromptInjection());
        Assert.DoesNotContain("子任务", toolSet.IterationPromptInjection());

        toolSet.Reset();
        Assert.Null(toolSet.IterationPromptInjection());
    }

    [Fact]
    public async Task MissingPlan_IsRejected()
    {
        TodoListToolSet toolSet = new();

        await Assert.ThrowsAsync<JsonException>(() => InvokeAsync(toolSet, "{}"));
    }

    [Fact]
    public async Task MultipleInProgressSteps_AreRejected()
    {
        TodoListToolSet toolSet = new();
        string arguments =
            """{"plan":[{"step":"步骤一","status":"in_progress"},{"step":"步骤二","status":"in_progress"}]}""";

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(toolSet, arguments));

        Assert.Contains("最多只能有一个 in_progress", exception.Message);
    }

    [Fact]
    public async Task InvalidStepAndOversizedPlan_AreRejected()
    {
        TodoListToolSet toolSet = new();

        ArgumentException emptyStep = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(toolSet, """{"plan":[{"step":" ","status":"pending"}]}"""));
        Assert.Contains("step 不能为空", emptyStep.Message);

        string longStep = new('x', 501);
        string longStepArguments = JsonSerializer.Serialize(
            new { plan = new[] { new { step = longStep, status = "pending" } } });
        ArgumentException oversizedStep = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(toolSet, longStepArguments));
        Assert.Contains("不能超过 500", oversizedStep.Message);

        object[] tooManySteps = Enumerable.Range(0, 501)
            .Select(static index => (object)new { step = $"步骤 {index}", status = "pending" })
            .ToArray();
        string oversizedPlanArguments = JsonSerializer.Serialize(new { plan = tooManySteps });
        ArgumentException oversizedPlan = await Assert.ThrowsAsync<ArgumentException>(
            () => InvokeAsync(toolSet, oversizedPlanArguments));
        Assert.Contains("最多 500 个步骤", oversizedPlan.Message);
    }

    [Fact]
    public async Task ConcurrentUpdates_AreSerializedAndReturnValidPlans()
    {
        TodoListToolSet toolSet = new();
        Task<string>[] updates = Enumerable.Range(0, 32)
            .Select(index => InvokeAsync(
                toolSet,
                JsonSerializer.Serialize(new
                {
                    plan = new[] { new { step = $"步骤 {index}", status = "in_progress" } },
                })))
            .ToArray();

        string[] results = await Task.WhenAll(updates);

        Assert.Equal(32, results.Length);
        Assert.All(results, result =>
        {
            Assert.Contains("当前计划（共 1 项）", result);
            Assert.Contains("[in_progress] 步骤", result);
        });
    }

    private static Task<string> InvokeAsync(TodoListToolSet toolSet, string arguments)
    {
        return toolSet.InvokeAsync(
            CancellationToken.None,
            new ToolCall("call_todo", "todo_list", arguments),
            static _ => { });
    }

    private static bool HasRequiredProperty(JsonElement schema, string propertyName)
    {
        return schema.TryGetProperty("required", out JsonElement required)
            && required.EnumerateArray().Any(value => value.GetString() == propertyName);
    }
}
