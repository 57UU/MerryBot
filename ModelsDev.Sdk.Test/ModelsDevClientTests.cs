using System.Net;
using System.Text.Json;
using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk.Test;

public class ModelsDevClientTests
{
    [Fact]
    public void IsLoaded_False_BeforeAnyLoad()
    {
        var client = new ModelsDevClient();

        Assert.False(client.IsLoaded);
    }

    [Fact]
    public void Query_BeforeLoad_ThrowsInvalidOperationException()
    {
        var client = new ModelsDevClient();

        Assert.Throws<InvalidOperationException>(() => client.GetAllProviders());
        Assert.Throws<InvalidOperationException>(() => client.GetProvider("openai"));
        Assert.Throws<InvalidOperationException>(() => client.GetModels("openai"));
        Assert.Throws<InvalidOperationException>(() => client.GetModel("openai", "gpt-4o"));
        Assert.Throws<InvalidOperationException>(() => client.FindModels(_ => true));
        Assert.Throws<InvalidOperationException>(() => client.GetAllModels());
        Assert.Throws<InvalidOperationException>(() => client.GetProviderDictionary());
    }

    [Fact]
    public void LoadFromJson_PopulatesData()
    {
        var client = new ModelsDevClient();
        client.LoadFromJson(TestData.Json);

        Assert.True(client.IsLoaded);
        Assert.Equal(2, client.GetAllProviders().Count);
    }

    [Fact]
    public void LoadFromJson_InvalidJson_ThrowsJsonException()
    {
        var client = new ModelsDevClient();

        Assert.Throws<JsonException>(() => client.LoadFromJson("{ not json"));
    }

    [Fact]
    public void LoadFromJson_NullJson_ThrowsJsonException()
    {
        var client = new ModelsDevClient();

        Assert.Throws<JsonException>(() => client.LoadFromJson("null"));
    }

    [Fact]
    public async Task LoadAsync_UsesHttpClientAndPopulatesData()
    {
        var handler = new TestData.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.Json),
        });
        var client = new ModelsDevClient(new HttpClient(handler));

        await client.LoadAsync();

        Assert.True(client.IsLoaded);
        Assert.Equal(5, client.GetAllModels().Count);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsRawCatalogWithoutLoadingIt()
    {
        var handler = new TestData.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.Json),
        });
        var client = new ModelsDevClient(new HttpClient(handler));

        var json = await client.DownloadAsync();

        Assert.Equal(TestData.Json, json);
        Assert.False(client.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_HttpFailure_ThrowsHttpRequestException()
    {
        var handler = new TestData.StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new ModelsDevClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        var handler = new TestData.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.Json),
        });
        var client = new ModelsDevClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadAsync(cts.Token));
    }

    [Fact]
    public void LoadFromJson_CanBeCalledMultipleTimes_ToRefreshData()
    {
        var client = new ModelsDevClient();
        client.LoadFromJson(TestData.Json);
        client.LoadFromJson("""{"new":{"id":"new","name":"New","models":{}}}""");

        Assert.Single(client.GetAllProviders());
        Assert.Equal("New", client.GetProvider("new")!.Name);
    }

    [Fact]
    public void GetAllProviders_ReturnsAllProviders()
    {
        var client = TestData.CreateLoadedClient();

        var ids = client.GetAllProviders().Select(p => p.Id).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "deepseek", "openai" }, ids);
    }

    [Fact]
    public void GetProvider_ReturnsProvider()
    {
        var client = TestData.CreateLoadedClient();

        var provider = client.GetProvider("openai");

        Assert.NotNull(provider);
        Assert.Equal("OpenAI", provider.Name);
    }

    [Fact]
    public void GetProvider_Missing_ReturnsNull()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Null(client.GetProvider("nonexistent"));
    }

    [Fact]
    public void GetProviderOrThrow_ReturnsProvider()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal("DeepSeek", client.GetProviderOrThrow("deepseek").Name);
    }

    [Fact]
    public void GetProviderOrThrow_Missing_ThrowsKeyNotFoundException()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Throws<KeyNotFoundException>(() => client.GetProviderOrThrow("nonexistent"));
    }

    [Fact]
    public void GetModels_ReturnsModelsForProvider()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.GetModels("openai").Count);
    }

    [Fact]
    public void GetModels_UnknownProvider_ReturnsEmptyList()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Empty(client.GetModels("nonexistent"));
    }

    [Fact]
    public void GetModel_ReturnsModel()
    {
        var client = TestData.CreateLoadedClient();

        var model = client.GetModel("openai", "gpt-4o");

        Assert.NotNull(model);
        Assert.Equal("GPT-4o", model.Name);
        Assert.Equal(128_000, model.Limit!.Context);
    }

    [Fact]
    public void GetModel_Missing_ReturnsNull()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Null(client.GetModel("openai", "missing"));
        Assert.Null(client.GetModel("missing", "gpt-4o"));
    }

    [Fact]
    public void FindModelById_PartialCaseInsensitiveMatch()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.FindModelById("GPT-4O");

        Assert.Single(results);
        Assert.Equal("openai", results[0].ProviderId);
        Assert.Equal("gpt-4o", results[0].Model.Id);
    }

    [Fact]
    public void FindModelById_NoMatch_ReturnsEmpty()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Empty(client.FindModelById("nonexistent"));
    }

    [Fact]
    public void FindModels_UsesPredicate()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.FindModels(m => m.Cost is not null && m.Cost.Input > 1m);

        Assert.Equal(2, results.Count); // gpt-4o (2.5) and o3 (2.0)
    }

    [Fact]
    public void FindModelsByModality_MatchesInputCaseInsensitive()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.FindModelsByModality("IMAGE");

        Assert.Single(results);
        Assert.Equal("gpt-4o", results[0].Model.Id);
    }

    [Fact]
    public void FindModelsByModality_NoMatch_ReturnsEmpty()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Empty(client.FindModelsByModality("video"));
    }

    [Fact]
    public void FindReasoningModels_FindsTwoModels()
    {
        var client = TestData.CreateLoadedClient();

        var results = client.FindReasoningModels();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Model.Id == "o3");
        Assert.Contains(results, r => r.Model.Id == "deepseek/deepseek-reasoner");
    }

    [Fact]
    public void FindToolCallModels_FindsThreeModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.FindToolCallModels().Count);
    }

    [Fact]
    public void FindOpenWeightModels_FindsThreeModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.FindOpenWeightModels().Count);
    }

    [Fact]
    public void FindModelsByCost_FiltersByInputCost()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(3, client.FindModelsByCost(1m).Count);
        Assert.Equal(2, client.FindModelsByCost(0.5m).Count);
        Assert.Single(client.FindModelsByCost(0.1m)); // deepseek-lite (free)
    }

    [Fact]
    public void FindModelsByContextSize_FiltersByContextWindow()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(4, client.FindModelsByContextSize(64_000).Count);
        Assert.Equal(2, client.FindModelsByContextSize(100_000).Count);
        Assert.Empty(client.FindModelsByContextSize(300_000));
    }

    [Fact]
    public void FindModelsByFamily_PartialCaseInsensitiveMatch()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(2, client.FindModelsByFamily("DEEPSEEK").Count);
        Assert.Single(client.FindModelsByFamily("o-series"));
    }

    [Fact]
    public void GetAllModels_ReturnsAllFiveModels()
    {
        var client = TestData.CreateLoadedClient();

        Assert.Equal(5, client.GetAllModels().Count);
    }

    [Fact]
    public void GetProviderDictionary_ReturnsUnderlyingData()
    {
        var client = TestData.CreateLoadedClient();

        var dict = client.GetProviderDictionary();

        Assert.Equal(2, dict.Count);
        Assert.Same(client.GetProvider("openai"), dict["openai"]);
    }
}
