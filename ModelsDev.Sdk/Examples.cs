// =============================================================================
// ModelsDev.Sdk - Usage Examples
// =============================================================================
//
// Basic setup:
//
//   var client = new ModelsDevClient();
//   await client.LoadAsync();
//
// =============================================================================
// Querying Providers
// =============================================================================
//
//   // Get all providers
//   var providers = client.GetAllProviders();
//   foreach (var p in providers)
//       Console.WriteLine($"{p.Id}: {p.Name} ({p.Models.Count} models)");
//
//   // Get a specific provider
//   var openai = client.GetProvider("openai");
//   Console.WriteLine($"API: {openai?.Api}");
//
// =============================================================================
// Querying Models
// =============================================================================
//
//   // Get all models from a provider
//   var anthropicModels = client.GetModels("anthropic");
//
//   // Get a specific model
//   var claude = client.GetModel("anthropic", "claude-sonnet-4-20250514");
//
//   // Search models by ID (partial, case-insensitive)
//   var geminiResults = client.FindModelById("gemini");
//
// =============================================================================
// Finding models with predicates
// =============================================================================
//
//   // All reasoning models
//   var reasoning = client.FindReasoningModels();
//
//   // All vision models
//   var vision = client.FindModelsByModality("image");
//
//   // All models with 1M+ context
//   var longContext = client.FindModelsByContextSize(1_000_000);
//
//   // All free models
//   var free = client.FindModelsByCost(0m);
//
//   // Custom predicate
//   var bigContextToolCall = client.FindModels(m =>
//       m.Limit?.Context >= 200_000 && m.ToolCall && m.Reasoning);
//
// =============================================================================
// Fluent Query Builder
// =============================================================================
//
//   // Chain filters for complex queries
//   var results = client.Query()
//       .WithReasoning()
//       .WithToolCall()
//       .WithContextAtLeast(100_000)
//       .WithMaxInputCost(5m)
//       .Active()
//       .Execute();
//
//   // Vision models with tool calling
//   var visionToolModels = client.Query()
//       .WithVision()
//       .WithToolCall()
//       .ToList();
//
//   // Models from a specific provider with certain criteria
//   var openaiReasoning = client.Query()
//       .FromProvider("openai")
//       .WithReasoning()
//       .ToList();
//
//   // Free models that support image input
//   var freeVision = client.Query()
//       .Free()
//       .WithVision()
//       .ToList();
//
//   // All open-weight reasoning models with 100K+ context
//   var openReasoning = client.Query()
//       .WithOpenWeights()
//       .WithReasoning()
//       .WithContextAtLeast(100_000)
//       .ToList();
//
// =============================================================================
// Loading from local JSON (for offline/testing)
// =============================================================================
//
//   var json = await File.ReadAllTextAsync("api.json");
//   var client = new ModelsDevClient();
//   client.LoadFromJson(json);
//
