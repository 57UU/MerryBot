using BotPlugin;
using LlmBackend;
using MerryBot.WebUI;
using MerryBot.WebUI.Api;

namespace MerryBot.Test;

/// <summary>
/// LlmProviderService：未加载时抛 InvalidOperationException（对等原来 API 404），
/// 配置映射透传 ApiFormat 名/能力串/Key 指纹（Key 明文类型上就不存在），保存请求参数映射正确。
/// </summary>
public sealed class LlmProviderServiceTests
{
    private sealed class FakeManager : ILlmProviderManagementService
    {
        public LlmProviderConfiguration? ConfigurationToReturn { get; set; }
        public LlmModelSaveCommand? CapturedSaveModel { get; private set; }
        public string? CapturedSaveModelId { get; private set; }
        public string? CapturedDefaultModelId { get; private set; }

        public Task<LlmProviderConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ConfigurationToReturn ?? throw new InvalidOperationException("no configuration"));

        public Task<LlmProviderConfiguration> ImportCatalogModelAsync(
            LlmProviderCatalogImportCommand command, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task SaveProviderAsync(string id, LlmProviderSaveCommand command, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteProviderAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task SaveModelAsync(string id, LlmModelSaveCommand command, CancellationToken cancellationToken = default)
        {
            CapturedSaveModelId = id;
            CapturedSaveModel = command;
            return Task.CompletedTask;
        }

        public Task DeleteModelAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<LlmProviderConfigurationKey> SaveKeyAsync(
            LlmProviderKeySaveCommand command, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteKeyAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task SetDefaultModelAsync(string modelId, CancellationToken cancellationToken = default)
        {
            CapturedDefaultModelId = modelId;
            return Task.CompletedTask;
        }
    }

    private static LlmProviderConfiguration SampleConfiguration() => new(
        "p1/m1",
        [
            new LlmProviderConfigurationProvider(
                "p1",
                "测试 Provider",
                "https://api.example.com/v1",
                LlmApiFormat.OpenAiResponses,
                true,
                "openai",
                [
                    new LlmProviderConfigurationModel(
                        "p1/m1",
                        "p1",
                        "gpt-x",
                        128000,
                        16000,
                        LlmModelCapabilities.Text,
                        true,
                        null,
                        "high",
                        true,
                        [new LlmReasoningOption("effort", ["low", "high"])]),
                ],
                [
                    new LlmProviderConfigurationKey("k1", "abcd-末四位", true, DateTimeOffset.UtcNow),
                ]),
        ]);

    [Fact]
    public void Null_Registry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LlmProviderService(null!));
    }

    [Fact]
    public async Task Missing_Manager_Throws_InvalidOperation()
    {
        LlmProviderService service = new(new WebUiServiceRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetConfigurationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveProviderAsync("p", new LlmSaveProviderRequest("n", "https://x", null, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDefaultModelAsync("p1/m1"));
    }

    [Fact]
    public async Task Missing_Catalog_Throws_InvalidOperation()
    {
        LlmProviderService service = new(new WebUiServiceRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogProvidersAsync(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogStatusAsync());
    }

    [Fact]
    public async Task GetConfiguration_Maps_Format_Capabilities_And_Key_Fingerprint()
    {
        FakeManager manager = new() { ConfigurationToReturn = SampleConfiguration() };
        LlmProviderService service = new(new WebUiServiceRegistry { LlmProviders = manager });

        LlmConfigSnapshot snapshot = await service.GetConfigurationAsync();

        Assert.Equal("p1/m1", snapshot.DefaultModelId);
        LlmProviderDto provider = Assert.Single(snapshot.Providers);
        Assert.Equal("openai-responses", provider.ApiFormat);
        LlmModelDto model = Assert.Single(provider.Models);
        Assert.Equal("Text", model.Capabilities);
        Assert.Equal("high", model.ReasoningEffort);
        Assert.True(model.EnablePromptCache);
        LlmReasoningOptionDto option = Assert.Single(model.ReasoningOptions!);
        Assert.Equal("effort", option.Type);
        Assert.Equal(["low", "high"], option.Values);
        // Key 只出指纹：快照类型上就没有明文字段
        LlmKeyDto key = Assert.Single(provider.Keys);
        Assert.Equal("abcd-末四位", key.Fingerprint);
    }

    [Fact]
    public async Task SaveModel_Maps_Capabilities_And_Reasoning_Options()
    {
        FakeManager manager = new() { ConfigurationToReturn = SampleConfiguration() };
        LlmProviderService service = new(new WebUiServiceRegistry { LlmProviders = manager });

        await service.SaveModelAsync("p1/m1", new LlmSaveModelRequest(
            "p1",
            "gpt-x",
            128000,
            16000,
            (int)(LlmModelCapabilities.Text | LlmModelCapabilities.ToolCalls),
            true,
            "high",
            true,
            [new LlmReasoningOptionDto("effort", ["low"])]));

        Assert.Equal("p1/m1", manager.CapturedSaveModelId);
        Assert.NotNull(manager.CapturedSaveModel);
        Assert.Equal("p1", manager.CapturedSaveModel.ProviderId);
        Assert.Equal("gpt-x", manager.CapturedSaveModel.RemoteModelId);
        Assert.Equal(
            LlmModelCapabilities.Text | LlmModelCapabilities.ToolCalls,
            manager.CapturedSaveModel.Capabilities);
        Assert.Equal("high", manager.CapturedSaveModel.ReasoningEffort);
        Assert.True(manager.CapturedSaveModel.EnablePromptCache);
        LlmReasoningOption capturedOption = Assert.Single(manager.CapturedSaveModel.ReasoningOptions!);
        Assert.Equal("effort", capturedOption.Type);
        Assert.Equal(["low"], capturedOption.Values);
    }

    [Fact]
    public async Task SetDefaultModel_Passes_Id_Through()
    {
        FakeManager manager = new() { ConfigurationToReturn = SampleConfiguration() };
        LlmProviderService service = new(new WebUiServiceRegistry { LlmProviders = manager });

        // 含 / 的模型 Id 直传原文（原来 HTTP catch-all 的 %2F 转码 hack 已消失）
        await service.SetDefaultModelAsync("p1/m1");

        Assert.Equal("p1/m1", manager.CapturedDefaultModelId);
    }
}
