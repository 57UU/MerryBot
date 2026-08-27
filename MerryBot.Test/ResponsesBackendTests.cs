using System.Text.Json;
using LlmBackend;

namespace MerryBot.Test;

public sealed class ResponsesBackendTests
{
    [Fact]
    public void BuildInput_UsesTopLevelFunctionCallAndOutputItems()
    {
        IList<Message> messages =
        [
            new Message
            {
                role = Role.Assistant,
                toolCalls = [new ToolCall("call_1", "send_markdown", "{\"markdown\":\"hello\"}")],
            },
            new Message
            {
                role = Role.Tool,
                toolCallId = "call_1",
                content = [new MessagePartText { text = "{\"error\":\"failed\"}" }],
            },
        ];

        List<object> input = ResponsesBackend.BuildInput(messages);
        JsonElement json = JsonSerializer.SerializeToElement(input);

        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(2, json.GetArrayLength());

        JsonElement functionCall = json[0];
        Assert.Equal("function_call", functionCall.GetProperty("type").GetString());
        Assert.Equal("call_1", functionCall.GetProperty("call_id").GetString());
        Assert.Equal("send_markdown", functionCall.GetProperty("name").GetString());
        Assert.False(functionCall.TryGetProperty("tool_calls", out _));

        JsonElement functionOutput = json[1];
        Assert.Equal("function_call_output", functionOutput.GetProperty("type").GetString());
        Assert.Equal("call_1", functionOutput.GetProperty("call_id").GetString());
        Assert.Equal("{\"error\":\"failed\"}", functionOutput.GetProperty("output").GetString());
    }
}
