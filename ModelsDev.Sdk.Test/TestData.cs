namespace ModelsDev.Sdk.Test;

/// <summary>
/// Shared sample data in models.dev/api.json format for unit tests.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Two providers ("openai", "deepseek") with five models total, covering
    /// every field and capability used by the SDK's query methods. The JSON
    /// deliberately contains a comment line and a trailing comma so that the
    /// SDK's lenient JSON options are exercised too.
    /// </summary>
    public const string Json = """
        {
          // comment lines are skipped by the SDK's JSON options
          "openai": {
            "id": "openai",
            "name": "OpenAI",
            "env": ["OPENAI_API_KEY"],
            "npm": "@ai-sdk/openai",
            "api": "https://api.openai.com/v1",
            "doc": "https://platform.openai.com/docs",
            "models": {
              "gpt-4o": {
                "id": "gpt-4o",
                "name": "GPT-4o",
                "description": "Flagship multimodal model",
                "family": "gpt-4o",
                "attachment": true,
                "reasoning": false,
                "reasoning_options": [],
                "tool_call": true,
                "structured_output": true,
                "temperature": true,
                "interleaved": { "field": "reasoning_content" },
                "knowledge": "2024-10",
                "release_date": "2024-05-13",
                "last_updated": "2025-07-01",
                "modalities": { "input": ["text", "image", "audio"], "output": ["text"] },
                "open_weights": false,
                "limit": { "context": 128000, "output": 16384 },
                "cost": { "input": 2.5, "output": 10.0, "cache_read": 1.25, "cache_write": 3.75 }
              },
              "o3": {
                "id": "o3",
                "name": "OpenAI o3",
                "family": "o-series",
                "reasoning": true,
                "reasoning_options": [
                  { "type": "toggle" },
                  { "type": "effort", "values": ["low", "medium", "high"] }
                ],
                "tool_call": true,
                "structured_output": true,
                "modalities": { "input": ["text"], "output": ["text"] },
                "limit": { "context": 200000, "output": 100000 },
                "cost": { "input": 2.0, "output": 8.0 },
                "status": "preview"
              }
            }
          },
          "deepseek": {
            "id": "deepseek",
            "name": "DeepSeek",
            "env": ["DEEPSEEK_API_KEY"],
            "npm": "@ai-sdk/deepseek",
            "models": {
              "deepseek-chat": {
                "id": "deepseek/deepseek-chat",
                "name": "DeepSeek Chat",
                "family": "deepseek-chat",
                "tool_call": true,
                "structured_output": true,
                "modalities": { "input": ["text"], "output": ["text"] },
                "open_weights": true,
                "limit": { "context": 64000, "output": 8192 },
                "cost": { "input": 0.27, "output": 1.10 }
              },
              "deepseek-reasoner": {
                "id": "deepseek/deepseek-reasoner",
                "name": "DeepSeek Reasoner",
                "family": "deepseek-reasoner",
                "reasoning": true,
                "modalities": { "input": ["text"], "output": ["text"] },
                "open_weights": true,
                "limit": { "context": 64000, "output": 8192 },
                "cost": { "input": 0.55, "output": 2.19 },
                "status": "deprecated"
              },
              "deepseek-lite": {
                "id": "deepseek-lite",
                "name": "DeepSeek Lite",
                "open_weights": true,
                "limit": { "context": 32000, "output": 4096 },
                "cost": { "input": 0, "output": 0 }
              }
            }
          },
        }
        """;

    /// <summary>
    /// Creates a client already loaded with the sample data.
    /// </summary>
    public static ModelsDevClient CreateLoadedClient()
    {
        var client = new ModelsDevClient();
        client.LoadFromJson(Json);
        return client;
    }

    /// <summary>
    /// HttpMessageHandler stub that always returns the given response.
    /// </summary>
    public sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
