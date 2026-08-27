using System.Net;
using System.Net.Sockets;
using System.Text;
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
                content = [new MessagePartText { text = "准备调用工具" }],
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
        Assert.Equal(3, json.GetArrayLength());

        JsonElement assistant = json[0];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("output_text", assistant.GetProperty("content")[0].GetProperty("type").GetString());

        JsonElement functionCall = json[1];
        Assert.Equal("function_call", functionCall.GetProperty("type").GetString());
        Assert.Equal("call_1", functionCall.GetProperty("call_id").GetString());
        Assert.Equal("send_markdown", functionCall.GetProperty("name").GetString());
        Assert.False(assistant.TryGetProperty("tool_calls", out _));

        JsonElement functionOutput = json[2];
        Assert.Equal("function_call_output", functionOutput.GetProperty("type").GetString());
        Assert.Equal("call_1", functionOutput.GetProperty("call_id").GetString());
        Assert.Equal("{\"error\":\"failed\"}", functionOutput.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    public void BuildInput_NormalizesInvalidFunctionArguments(string arguments)
    {
        IList<Message> messages =
        [
            new Message
            {
                role = Role.Assistant,
                toolCalls = [new ToolCall("call_1", "send_markdown", arguments)],
            },
        ];

        JsonElement json = JsonSerializer.SerializeToElement(ResponsesBackend.BuildInput(messages));

        Assert.Equal("{}", json[0].GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task GenerateStream_UsesCompleteArgumentsFromDoneEvents()
    {
        using TcpListener portProbe = new(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            string[] events =
            [
                JsonSerializer.Serialize(new
                {
                    type = "response.output_item.added",
                    item_id = "item_1",
                    output_index = 0,
                    item = new { type = "function_call", id = "item_1", call_id = "call_1", name = "send_markdown" },
                }),
                JsonSerializer.Serialize(new
                {
                    type = "response.function_call_arguments.delta",
                    item_id = "item_1",
                    output_index = 0,
                    delta = "{\\\"markdown\\\":\\\"partial",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "response.function_call_arguments.done",
                    item_id = "item_1",
                    output_index = 0,
                    name = "send_markdown",
                    arguments = "{\"markdown\":\"完整参数\"}",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "response.output_item.done",
                    output_index = 0,
                    item = new
                    {
                        type = "function_call",
                        id = "item_1",
                        call_id = "call_1",
                        name = "send_markdown",
                        arguments = "{\"markdown\":\"完整参数\"}",
                    },
                }),
                JsonSerializer.Serialize(new { type = "response.completed" }),
            ];
            await using Stream stream = context.Response.OutputStream;
            foreach (string eventData in events)
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"data: {eventData}\n\n");
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        });

        try
        {
            ResponsesBackend backend = new($"http://127.0.0.1:{port}", "test-key", "test-model");
            RecordingSink sink = new();
            await backend.GenerateStream(
                sink,
                [Message.User("test")],
                string.Empty,
                new LlmOptions(Tools: []),
                CancellationToken.None);

            ToolCall call = Assert.Single(sink.Response!.ToolCalls!);
            Assert.Equal("call_1", call.Id);
            Assert.Equal("send_markdown", call.Name);
            Assert.Equal("{\"markdown\":\"完整参数\"}", call.Arguments);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private sealed class RecordingSink : IStreamSink
    {
        public GenerateResponse? Response { get; private set; }

        public void OnTextDelta(string delta) { }

        public void OnReasoningDelta(string delta) { }

        public void OnCompleted(GenerateResponse response, TokenUsage usage) => Response = response;
    }
}
