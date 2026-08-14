using System.Text.Json;
using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk;

/// <summary>
/// Type-safe client for querying AI model metadata from models.dev/api.json.
/// </summary>
/// <example>
/// <code>
/// var client = new ModelsDevClient();
/// await client.LoadAsync();
///
/// // Get all providers
/// var providers = client.GetAllProviders();
///
/// // Get a specific provider
/// var openai = client.GetProvider("openai");
///
/// // Get a specific model
/// var gpt4o = client.GetModel("openai", "gpt-4o");
///
/// // Find all reasoning models
/// var reasoningModels = client.FindModels(m => m.Reasoning);
///
/// // Find vision models across all providers
/// var visionModels = client.FindModelsByModality("image");
/// </code>
/// </example>
public sealed class ModelsDevClient
{
    private const string DefaultApiUrl = "https://models.dev/api.json";

    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private IReadOnlyDictionary<string, Provider>? _providers;

    /// <summary>
    /// Creates a new client with default settings.
    /// </summary>
    public ModelsDevClient()
        : this(new HttpClient(), DefaultApiUrl)
    {
    }

    /// <summary>
    /// Creates a new client with a custom HttpClient.
    /// </summary>
    public ModelsDevClient(HttpClient httpClient)
        : this(httpClient, DefaultApiUrl)
    {
    }

    /// <summary>
    /// Creates a new client with a custom HttpClient and API URL.
    /// </summary>
    public ModelsDevClient(HttpClient httpClient, string apiUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
    }

    /// <summary>
    /// Whether data has been loaded from the API.
    /// </summary>
    public bool IsLoaded => _providers is not null;

    /// <summary>
    /// Downloads and parses the model data from models.dev. Must be called before any query.
    /// Can be called multiple times to refresh data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="HttpRequestException">Thrown when the HTTP request fails.</exception>
    /// <exception cref="JsonException">Thrown when the response cannot be parsed.</exception>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await _httpClient.GetStringAsync(_apiUrl, cancellationToken);
        var providers = JsonSerializer.Deserialize<Dictionary<string, Provider>>(json, JsonOptions.Default);

        if (providers is null)
            throw new JsonException("Failed to deserialize provider data from models.dev");

        _providers = providers;
    }

    /// <summary>
    /// Loads data from a pre-fetched JSON string (useful for testing or offline scenarios).
    /// </summary>
    public void LoadFromJson(string json)
    {
        var providers = JsonSerializer.Deserialize<Dictionary<string, Provider>>(json, JsonOptions.Default);
        _providers = providers ?? throw new JsonException("Failed to deserialize provider data");
    }

    /// <summary>
    /// Gets all providers.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when data has not been loaded.</exception>
    public IReadOnlyList<Provider> GetAllProviders()
    {
        EnsureLoaded();
        return _providers!.Values.ToList();
    }

    /// <summary>
    /// Gets a provider by its ID (e.g. "openai", "anthropic").
    /// </summary>
    /// <returns>The provider, or null if not found.</returns>
    public Provider? GetProvider(string providerId)
    {
        EnsureLoaded();
        return _providers!.GetValueOrDefault(providerId);
    }

    /// <summary>
    /// Gets a provider by ID, throwing if not found.
    /// </summary>
    public Provider GetProviderOrThrow(string providerId)
    {
        return GetProvider(providerId)
            ?? throw new KeyNotFoundException($"Provider '{providerId}' not found.");
    }

    /// <summary>
    /// Gets all models for a specific provider.
    /// </summary>
    public IReadOnlyList<ModelInfo> GetModels(string providerId)
    {
        EnsureLoaded();
        var provider = _providers!.GetValueOrDefault(providerId);
        return provider?.Models.Values.ToList() ?? [];
    }

    /// <summary>
    /// Gets a specific model by provider ID and model ID.
    /// </summary>
    /// <returns>The model info, or null if not found.</returns>
    public ModelInfo? GetModel(string providerId, string modelId)
    {
        EnsureLoaded();
        var provider = _providers!.GetValueOrDefault(providerId);
        return provider?.Models.GetValueOrDefault(modelId);
    }

    /// <summary>
    /// Searches for a model across all providers by model ID (partial match).
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModelById(string query)
    {
        EnsureLoaded();
        var results = new List<(string, ModelInfo)>();
        foreach (var (providerId, provider) in _providers!)
        {
            foreach (var (modelId, model) in provider.Models)
            {
                if (modelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || model.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((providerId, model));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Finds models matching a predicate across all providers.
    /// Returns pairs of (ProviderId, ModelInfo).
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModels(Func<ModelInfo, bool> predicate)
    {
        EnsureLoaded();
        var results = new List<(string, ModelInfo)>();
        foreach (var (providerId, provider) in _providers!)
        {
            foreach (var (modelId, model) in provider.Models)
            {
                if (predicate(model))
                    results.Add((providerId, model));
            }
        }
        return results;
    }

    /// <summary>
    /// Finds models that support a specific input modality (e.g. "image", "audio", "video", "pdf").
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModelsByModality(string inputModality)
    {
        return FindModels(m =>
            m.Modalities?.Input.Any(i => i.Equals(inputModality, StringComparison.OrdinalIgnoreCase)) == true);
    }

    /// <summary>
    /// Finds models that support reasoning/chain-of-thought.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindReasoningModels()
    {
        return FindModels(m => m.Reasoning);
    }

    /// <summary>
    /// Finds models that support tool/function calling.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindToolCallModels()
    {
        return FindModels(m => m.ToolCall);
    }

    /// <summary>
    /// Finds models with open/available weights.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindOpenWeightModels()
    {
        return FindModels(m => m.OpenWeights == true);
    }

    /// <summary>
    /// Finds models within a cost range (input cost per million tokens in USD).
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModelsByCost(decimal maxInputCostPerMillion)
    {
        return FindModels(m => m.Cost is not null && m.Cost.Input <= maxInputCostPerMillion);
    }

    /// <summary>
    /// Finds models with context window at least as large as specified.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModelsByContextSize(int minContextTokens)
    {
        return FindModels(m => m.Limit is not null && m.Limit.Context >= minContextTokens);
    }

    /// <summary>
    /// Finds models by family name (partial, case-insensitive match).
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> FindModelsByFamily(string familyQuery)
    {
        return FindModels(m =>
            m.Family is not null && m.Family.Contains(familyQuery, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all models across all providers as a flat enumerable.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> GetAllModels()
    {
        EnsureLoaded();
        var results = new List<(string, ModelInfo)>();
        foreach (var (providerId, provider) in _providers!)
        {
            foreach (var model in provider.Models.Values)
            {
                results.Add((providerId, model));
            }
        }
        return results;
    }

    /// <summary>
    /// Gets the underlying provider dictionary (for advanced LINQ queries).
    /// </summary>
    public IReadOnlyDictionary<string, Provider> GetProviderDictionary()
    {
        EnsureLoaded();
        return _providers!;
    }

    private void EnsureLoaded()
    {
        if (_providers is null)
            throw new InvalidOperationException("Data has not been loaded. Call LoadAsync() or LoadFromJson() first.");
    }
}
