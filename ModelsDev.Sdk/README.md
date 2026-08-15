# ModelsDev.Sdk

Type-safe C# SDK for querying AI model metadata from [models.dev](https://models.dev/api.json).

## Quick Start

```csharp
using ModelsDev.Sdk;

var client = new ModelsDevClient();
await client.LoadAsync();

// Find all reasoning models with tool calling and 100K+ context
var results = client.Query()
    .WithReasoning()
    .WithToolCall()
    .WithContextAtLeast(100_000)
    .Execute();

foreach (var (providerId, model) in results)
    Console.WriteLine($"[{providerId}] {model.Name} — {model.Description}");
```

## Setup

```csharp
// Default — fetches from https://models.dev/api.json
var client = new ModelsDevClient();
await client.LoadAsync();

// With custom HttpClient
var client = new ModelsDevClient(myHttpClient);
await client.LoadAsync();

// Load from local JSON (offline/testing)
var json = await File.ReadAllTextAsync("api.json");
var client = new ModelsDevClient();
client.LoadFromJson(json);
```

## Querying Providers

```csharp
// Get all providers
IReadOnlyList<Provider> providers = client.GetAllProviders();

// Get a specific provider
Provider? openai = client.GetProvider("openai");

// Get provider or throw
Provider anthropic = client.GetProviderOrThrow("anthropic");
```

## Querying Models

```csharp
// Get all models from a provider
IReadOnlyList<ModelInfo> models = client.GetModels("openai");

// Get a specific model
ModelInfo? gpt4o = client.GetModel("openai", "gpt-4o");

// Search by ID (partial, case-insensitive)
var matches = client.FindModelById("gemini");
```

## Built-in Finders

```csharp
var reasoning     = client.FindReasoningModels();
var toolCall      = client.FindToolCallModels();
var vision        = client.FindModelsByModality("image");
var openWeights   = client.FindOpenWeightModels();
var cheap         = client.FindModelsByCost(1m);       // ≤$1/M input tokens
var longContext   = client.FindModelsByContextSize(1_000_000);
var geminiFamily  = client.FindModelsByFamily("gemini");

// Custom predicate
var custom = client.FindModels(m =>
    m.Limit?.Context >= 200_000
    && m.ToolCall
    && m.Reasoning
    && m.OpenWeights == true);
```

## Fluent Query Builder

```csharp
// Chain multiple filters
var models = client.Query()
    .WithReasoning()
    .WithToolCall()
    .WithVision()
    .WithContextAtLeast(100_000)
    .WithMaxInputCost(5m)
    .Active()
    .Execute();

// Scope to a single provider
var openaiModels = client.Query()
    .FromProvider("openai")
    .WithStructuredOutput()
    .ToList();

// Free vision models
var freeVision = client.Query()
    .Free()
    .WithVision()
    .ToList();

// Custom filter
var custom = client.Query()
    .Where(m => m.Limit?.Output >= 64_000)
    .WithFamily("claude")
    .ToList();
```

## Model Properties

```csharp
ModelInfo model = client.GetModel("openai", "gpt-4o")!;

model.Id              // "gpt-4o"
model.Name            // "GPT-4o"
model.Description     // "Fast flagship for multimodal apps..."
model.Family          // "gpt"
model.Attachment      // true
model.Reasoning       // false
model.ToolCall        // true
model.StructuredOutput // true
model.Temperature     // true
model.OpenWeights     // false
model.Status          // null or "deprecated", "preview"

model.Modalities.Input   // ["text", "image", "audio"]
model.Modalities.Output  // ["text"]

model.Limit.Context      // 128000
model.Limit.Output       // 16384

model.Cost.Input         // 2.50 (USD/M tokens)
model.Cost.Output        // 10.00
model.Cost.CacheRead     // 1.25
model.Cost.CacheWrite    // 2.50

model.ReleaseDate        // "2024-05-13"
model.LastUpdated        // "2025-01-15"
model.Knowledge          // "2025-01"

model.ReasoningOptions   // [{Type="toggle"}] or [{Type="effort", Values=["high","max"]}]
model.Interleaved.Field  // "reasoning_content"
```

## Provider Properties

```csharp
Provider provider = client.GetProvider("openai")!;

provider.Id     // "openai"
provider.Name   // "OpenAI"
provider.Env    // ["OPENAI_API_KEY"]
provider.Npm    // "@ai-sdk/openai"
provider.Api    // "https://api.openai.com/v1"
provider.Doc    // "https://platform.openai.com/docs"
provider.Models // IReadOnlyDictionary<string, ModelInfo>
```

## Result Type

All multi-result queries return `IReadOnlyList<(string ProviderId, ModelInfo Model)>` so you always know which provider a model belongs to.
