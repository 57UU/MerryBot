using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk;

/// <summary>
/// Fluent query builder for filtering models across providers.
/// </summary>
/// <example>
/// <code>
/// var results = await client.Query()
///     .WithReasoning()
///     .WithToolCall()
///     .WithContextAtLeast(100_000)
///     .WithMaxInputCost(5m)
///     .Execute();
/// </code>
/// </example>
public sealed class ModelQueryBuilder
{
    private readonly ModelsDevClient _client;
    private readonly List<Func<ModelInfo, bool>> _filters = [];

    internal ModelQueryBuilder(ModelsDevClient client)
    {
        _client = client;
    }

    /// <summary>Filter to models that support reasoning.</summary>
    public ModelQueryBuilder WithReasoning()
    {
        _filters.Add(m => m.Reasoning);
        return this;
    }

    /// <summary>Filter to models that support tool calling.</summary>
    public ModelQueryBuilder WithToolCall()
    {
        _filters.Add(m => m.ToolCall);
        return this;
    }

    /// <summary>Filter to models that support structured output.</summary>
    public ModelQueryBuilder WithStructuredOutput()
    {
        _filters.Add(m => m.StructuredOutput);
        return this;
    }

    /// <summary>Filter to models that support attachments.</summary>
    public ModelQueryBuilder WithAttachment()
    {
        _filters.Add(m => m.Attachment);
        return this;
    }

    /// <summary>Filter to models with open weights.</summary>
    public ModelQueryBuilder WithOpenWeights()
    {
        _filters.Add(m => m.OpenWeights == true);
        return this;
    }

    /// <summary>Filter to models that support a specific input modality.</summary>
    public ModelQueryBuilder WithInputModality(string modality)
    {
        _filters.Add(m =>
            m.Modalities?.Input.Any(i => i.Equals(modality, StringComparison.OrdinalIgnoreCase)) == true);
        return this;
    }

    /// <summary>Filter to models that support vision (image input).</summary>
    public ModelQueryBuilder WithVision()
    {
        return WithInputModality("image");
    }

    /// <summary>Filter to models with context window >= specified token count.</summary>
    public ModelQueryBuilder WithContextAtLeast(int minTokens)
    {
        _filters.Add(m => m.Limit is not null && m.Limit.Context >= minTokens);
        return this;
    }

    /// <summary>Filter to models with output limit >= specified token count.</summary>
    public ModelQueryBuilder WithOutputAtLeast(int minTokens)
    {
        _filters.Add(m => m.Limit is not null && m.Limit.Output >= minTokens);
        return this;
    }

    /// <summary>Filter to models with input cost per million tokens <= max.</summary>
    public ModelQueryBuilder WithMaxInputCost(decimal maxCost)
    {
        _filters.Add(m => m.Cost is not null && m.Cost.Input <= maxCost);
        return this;
    }

    /// <summary>Filter to free models (input cost == 0).</summary>
    public ModelQueryBuilder Free()
    {
        _filters.Add(m => m.Cost is not null && m.Cost.Input == 0);
        return this;
    }

    /// <summary>Filter to models by family (case-insensitive partial match).</summary>
    public ModelQueryBuilder WithFamily(string family)
    {
        _filters.Add(m =>
            m.Family is not null && m.Family.Contains(family, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>Filter to models from a specific provider.</summary>
    public ModelQueryBuilder FromProvider(string providerId)
    {
        _filters.Add(_ => true); // Marker; actual filtering done in Execute
        _providerFilter = providerId;
        return this;
    }

    private string? _providerFilter;

    /// <summary>Filter to models matching a custom predicate.</summary>
    public ModelQueryBuilder Where(Func<ModelInfo, bool> predicate)
    {
        _filters.Add(predicate);
        return this;
    }

    /// <summary>Filter to non-deprecated models.</summary>
    public ModelQueryBuilder Active()
    {
        _filters.Add(m => m.Status is null || !m.Status.Equals("deprecated", StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>
    /// Executes the query and returns matching models with their provider IDs.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ModelInfo Model)> Execute()
    {
        var allModels = _providerFilter is not null
            ? _client.GetModels(_providerFilter).Select(m => (ProviderId: _providerFilter, Model: m))
            : _client.GetAllModels();

        var results = allModels.Where(tuple => _filters.All(f => f(tuple.Model)));
        return results.ToList();
    }

    /// <summary>
    /// Executes the query and returns just the model info objects.
    /// </summary>
    public IReadOnlyList<ModelInfo> ToList()
    {
        return Execute().Select(t => t.Model).ToList();
    }
}

/// <summary>
/// Extension methods for ModelsDevClient to enable fluent query syntax.
/// </summary>
public static class ModelsDevClientExtensions
{
    /// <summary>
    /// Creates a new fluent query builder for filtering models.
    /// </summary>
    public static ModelQueryBuilder Query(this ModelsDevClient client)
    {
        return new ModelQueryBuilder(client);
    }
}
