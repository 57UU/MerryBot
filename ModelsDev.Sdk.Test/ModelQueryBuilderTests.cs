using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk.Test;

public class ModelQueryBuilderTests
{
    [Fact]
    public void Execute_NoFilters_ReturnsAllModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().Execute();

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void WithReasoning_ReturnsReasoningModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.Query().WithReasoning().Execute().Count);
    }

    [Fact]
    public void WithToolCall_ReturnsToolCallModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.Query().WithToolCall().Execute().Count);
    }

    [Fact]
    public void WithStructuredOutput_ReturnsStructuredOutputModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.Query().WithStructuredOutput().Execute().Count);
    }

    [Fact]
    public void WithAttachment_ReturnsAttachmentModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().WithAttachment().Execute();

        Assert.Single(results);
        Assert.Equal("gpt-4o", results[0].Model.Id);
    }

    [Fact]
    public void WithOpenWeights_ReturnsOpenWeightModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.Query().WithOpenWeights().Execute().Count);
    }

    [Fact]
    public void WithInputModality_MatchesCaseInsensitive()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Single(client.Query().WithInputModality("IMAGE").Execute());
        Assert.Empty(client.Query().WithInputModality("video").Execute());
    }

    [Fact]
    public void WithVision_ReturnsVisionModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Single(client.Query().WithVision().Execute());
    }

    [Fact]
    public void WithContextAtLeast_FiltersByContextWindow()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.Query().WithContextAtLeast(100_000).Execute().Count);
    }

    [Fact]
    public void WithOutputAtLeast_FiltersByOutputLimit()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(4, client.Query().WithOutputAtLeast(8192).Execute().Count);
    }

    [Fact]
    public void WithMaxInputCost_FiltersByCost()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.Query().WithMaxInputCost(0.5m).Execute().Count);
    }

    [Fact]
    public void Free_ReturnsOnlyZeroCostModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().Free().Execute();

        Assert.Single(results);
        Assert.Equal("deepseek-lite", results[0].Model.Id);
    }

    [Fact]
    public void WithFamily_PartialCaseInsensitiveMatch()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.Query().WithFamily("deepseek").Execute().Count);
    }

    [Fact]
    public void FromProvider_LimitsResultsToProvider()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().FromProvider("openai").Execute();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("openai", r.ProviderId));
    }

    [Fact]
    public void FromProvider_UnknownProvider_ReturnsEmpty()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Empty(client.Query().FromProvider("nonexistent").Execute());
    }

    [Fact]
    public void Where_UsesCustomPredicate()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().Where(m => m.Knowledge is not null).Execute();

        Assert.Single(results);
        Assert.Equal("gpt-4o", results[0].Model.Id);
    }

    [Fact]
    public void Active_ExcludesDeprecatedModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().Active().Execute();

        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(results, r => r.Model.Id == "deepseek/deepseek-reasoner");
    }

    [Fact]
    public void Active_KeepsPreviewModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query().Active().Execute();

        Assert.Contains(results, r => r.Model.Id == "o3");
    }

    [Fact]
    public void ChainedFilters_CombineWithAnd()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.Query()
            .FromProvider("deepseek")
            .WithReasoning()
            .Execute();

        Assert.Single(results);
        Assert.Equal("deepseek/deepseek-reasoner", results[0].Model.Id);
    }

    [Fact]
    public void ChainedFilters_NoOverlap_ReturnsEmpty()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Empty(client.Query().WithAttachment().WithOpenWeights().Execute());
    }

    [Fact]
    public void ToList_ReturnsOnlyModelInfoObjects()
    {
        var client = TestData.CreateLoadedClient();

        var models = client.Query().WithReasoning().ToList();

        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.IsType<ModelInfo>(m));
    }

    [Fact]
    public void Builder_IsReusable_AccumulatesFilters()
    {
        var client = TestData.CreateLoadedClient();
        var query = client.Query().FromProvider("deepseek");

        Assert.Equal(3, query.Execute().Count);
        Assert.Single(query.WithReasoning().Execute());
    }
}
