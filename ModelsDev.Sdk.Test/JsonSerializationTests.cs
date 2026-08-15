using System.Text.Json;
using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk.Test;

public class JsonSerializationTests
{
    [Fact]
    public void Deserialize_FullModel_PopulatesAllFields()
    {
        var client = TestData.CreateLoadedClient();
        var gpt4o = client.GetModel("openai", "gpt-4o")!;

        Assert.Equal("gpt-4o", gpt4o.Id);
        Assert.Equal("GPT-4o", gpt4o.Name);
        Assert.Equal("Flagship multimodal model", gpt4o.Description);
        Assert.Equal("gpt-4o", gpt4o.Family);
        Assert.True(gpt4o.Attachment);
        Assert.False(gpt4o.Reasoning);
        Assert.True(gpt4o.ToolCall);
        Assert.True(gpt4o.StructuredOutput);
        Assert.True(gpt4o.Temperature);
        Assert.True(gpt4o.Interleaved!.Enabled);
        Assert.Equal("reasoning_content", gpt4o.Interleaved!.Field);
        Assert.Equal("2024-10", gpt4o.Knowledge);
        Assert.Equal("2024-05-13", gpt4o.ReleaseDate);
        Assert.Equal("2025-07-01", gpt4o.LastUpdated);
        Assert.Equal(new[] { "text", "image", "audio" }, gpt4o.Modalities!.Input);
        Assert.Equal(new[] { "text" }, gpt4o.Modalities.Output);
        Assert.False(gpt4o.OpenWeights);
        Assert.Equal(128_000, gpt4o.Limit!.Context);
        Assert.Equal(16_384, gpt4o.Limit.Output);
        Assert.Equal(2.5m, gpt4o.Cost!.Input);
        Assert.Equal(10.0m, gpt4o.Cost.Output);
        Assert.Equal(1.25m, gpt4o.Cost.CacheRead);
        Assert.Equal(3.75m, gpt4o.Cost.CacheWrite);
        Assert.Null(gpt4o.Status);
    }

    [Fact]
    public void Deserialize_Defaults_ForMissingOptionalFields()
    {
        var client = TestData.CreateLoadedClient();
        var lite = client.GetModel("deepseek", "deepseek-lite")!;

        Assert.False(lite.Attachment);
        Assert.False(lite.Reasoning);
        Assert.False(lite.ToolCall);
        Assert.False(lite.StructuredOutput);
        Assert.False(lite.Temperature);
        Assert.Empty(lite.ReasoningOptions);
        Assert.Null(lite.Description);
        Assert.Null(lite.Family);
        Assert.Null(lite.Interleaved);
        Assert.Null(lite.Knowledge);
        Assert.Null(lite.ReleaseDate);
        Assert.Null(lite.LastUpdated);
        Assert.Null(lite.Modalities);
        Assert.Null(lite.Status);
        Assert.Equal(32_000, lite.Limit!.Context);
        Assert.Equal(4_096, lite.Limit.Output);
        Assert.Equal(0m, lite.Cost!.Input);
        Assert.Equal(0m, lite.Cost.Output);
        Assert.Null(lite.Cost.CacheRead);
        Assert.Null(lite.Cost.CacheWrite);
    }

    [Fact]
    public void Deserialize_ReasoningOptions_PopulatesTypesAndValues()
    {
        var client = TestData.CreateLoadedClient();
        var o3 = client.GetModel("openai", "o3")!;

        Assert.True(o3.Reasoning);
        Assert.Equal("preview", o3.Status);
        Assert.Equal(2, o3.ReasoningOptions.Count);
        Assert.Equal("toggle", o3.ReasoningOptions[0].Type);
        Assert.Equal("effort", o3.ReasoningOptions[1].Type);
        Assert.Equal(new[] { "low", "medium", "high" }, o3.ReasoningOptions[1].Values);
    }

    [Fact]
    public void Deserialize_Provider_PopulatesFields()
    {
        var client = TestData.CreateLoadedClient();
        var provider = client.GetProvider("openai")!;

        Assert.Equal("openai", provider.Id);
        Assert.Equal("OpenAI", provider.Name);
        Assert.Equal(new[] { "OPENAI_API_KEY" }, provider.Env);
        Assert.Equal("@ai-sdk/openai", provider.Npm);
        Assert.Equal("https://api.openai.com/v1", provider.Api);
        Assert.Equal("https://platform.openai.com/docs", provider.Doc);
        Assert.Equal(2, provider.Models.Count);
    }

    [Fact]
    public void JsonOptions_AllowTrailingCommasAndComments()
    {
        // The sample JSON contains a comment line and a trailing comma, so
        // successful loading proves the SDK's JSON options handle both.
        var client = TestData.CreateLoadedClient();

        Assert.Equal(5, client.GetAllModels().Count);

        // Parsing with comments/trailing commas must not corrupt field values.
        var gpt4o = client.GetModel("openai", "gpt-4o")!;
        Assert.Equal("GPT-4o", gpt4o.Name);
        Assert.Equal("Flagship multimodal model", gpt4o.Description);
        Assert.Equal(128_000, gpt4o.Limit!.Context);

        var lite = client.GetModel("deepseek", "deepseek-lite")!;
        Assert.Equal("DeepSeek Lite", lite.Name);
        Assert.Equal(32_000, lite.Limit!.Context);
    }

    [Fact]
    public void ModelInfo_CanBeDeserializedDirectly()
    {
        const string json = """
            {
              "id": "test/model",
              "name": "Test Model",
              "limit": { "context": 1000, "output": 500 },
              "cost": { "input": 1.5, "output": 3.0 }
            }
            """;

        var model = JsonSerializer.Deserialize<ModelInfo>(json);

        Assert.NotNull(model);
        Assert.Equal("test/model", model.Id);
        Assert.Equal("Test Model", model.Name);
        Assert.Equal(1000, model.Limit!.Context);
        Assert.Equal(500, model.Limit.Output);
        Assert.Equal(1.5m, model.Cost!.Input);
        Assert.Equal(3.0m, model.Cost.Output);
    }

    [Fact]
    public void Deserialize_InterleavedBoolean_RepresentsEnabledCapability()
    {
        const string json = """
            {
              "cloudflare-workers-ai": {
                "id": "cloudflare-workers-ai",
                "name": "Cloudflare Workers AI",
                "models": {
                  "@cf/nvidia/nemotron-3-120b-a12b": {
                    "id": "@cf/nvidia/nemotron-3-120b-a12b",
                    "name": "Nemotron",
                    "interleaved": true
                  }
                }
              }
            }
            """;

        var client = new ModelsDevClient();
        client.LoadFromJson(json);
        var interleaved = client
            .GetModel("cloudflare-workers-ai", "@cf/nvidia/nemotron-3-120b-a12b")!
            .Interleaved;

        Assert.NotNull(interleaved);
        Assert.True(interleaved.Enabled);
        Assert.Null(interleaved.Field);
    }
}
