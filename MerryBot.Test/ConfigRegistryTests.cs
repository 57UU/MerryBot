using CommonLib;
using MerryBot.WebUI;
using MerryBot.WebUI.Api;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace MerryBot.Test;

/// <summary>
/// WebUI 动态配置面板后端：List&lt;string&gt; 字段映射为 stringlist 类型并支持保存。
/// </summary>
public sealed class ConfigRegistryTests
{
    private sealed class SampleConfig
    {
        [ConfigDescription("视觉模型列表", "")]
        public List<string> VisionLlmModels { get; set; } = ["alpha", "beta"];

        [ConfigDescription("群号列表", "")]
        public List<long> GroupIds { get; set; } = [123, 456];

        [ConfigDescription("名字", "")]
        public string Name { get; set; } = "x";
    }

    private static (ConfigRegistry Registry, SampleConfig Config) CreateRegistry()
    {
        var registry = new ConfigRegistry(NullLogger<ConfigRegistry>.Instance);
        var config = new SampleConfig();
        registry.RegisterConfig("sample", config, () => Task.CompletedTask);
        return (registry, config);
    }

    [Fact]
    public void StringList_Field_Is_Exposed_As_StringList_Type()
    {
        var (registry, _) = CreateRegistry();

        var section = Assert.Single(registry.GetSnapshot());
        var vision = section.Fields.Single(static f => f.Key == nameof(SampleConfig.VisionLlmModels));
        Assert.Equal("stringlist", vision.Type);

        // 数值列表仍保持原 list 类型（回归）
        var groups = section.Fields.Single(static f => f.Key == nameof(SampleConfig.GroupIds));
        Assert.Equal("list", groups.Type);
    }

    [Fact]
    public async Task Save_StringList_Updates_Property()
    {
        var (registry, config) = CreateRegistry();

        var fields = new Dictionary<string, JsonElement>
        {
            ["VisionLlmModels"] = JsonDocument.Parse("[\"m1\",\"m2\"]").RootElement,
        };
        await registry.SaveAsync("sample", fields);

        Assert.Equal(["m1", "m2"], config.VisionLlmModels);
    }

    [Fact]
    public async Task Save_StringList_Rejects_Non_Array()
    {
        var (registry, config) = CreateRegistry();
        var original = new List<string>(config.VisionLlmModels);

        var fields = new Dictionary<string, JsonElement>
        {
            ["VisionLlmModels"] = JsonDocument.Parse("{\"not\":\"array\"}").RootElement,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => registry.SaveAsync("sample", fields));

        // 失败请求不应留下半更新状态
        Assert.Equal(original, config.VisionLlmModels);
    }

    [Fact]
    public async Task Save_NumberList_Still_Works()
    {
        var (registry, config) = CreateRegistry();

        var fields = new Dictionary<string, JsonElement>
        {
            ["GroupIds"] = JsonDocument.Parse("[7, 8]").RootElement,
        };
        await registry.SaveAsync("sample", fields);

        Assert.Equal([7L, 8L], config.GroupIds);
    }
}
