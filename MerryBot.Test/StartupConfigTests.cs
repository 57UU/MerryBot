using MerryBot;
using System.Text;

namespace MerryBot.Test;

/// <summary>
/// 启动配置（setting.toml，YAML 语法）加载：WebUI 监听地址的引导配置通道，不依赖 WebUI 本身。
/// </summary>
public sealed class StartupConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "startup-cfg-" + Guid.NewGuid().ToString("N"));

    public StartupConfigTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private void WriteConfig(string content)
        => File.WriteAllText(Path.Combine(_dir, StartupConfig.FileName), content, Encoding.UTF8);

    [Fact]
    public void Missing_File_Writes_Default_Template_And_Uses_Default_Address()
    {
        StartupConfig.Load(_dir);

        Assert.Equal(StartupConfig.DefaultWebAddress, StartupConfig.WebAddress);
        Assert.True(File.Exists(Path.Combine(_dir, StartupConfig.FileName)));
    }

    [Fact]
    public void Parses_Quoted_Web_Address()
    {
        WriteConfig("web-address: \"http://0.0.0.0:8080\"");
        StartupConfig.Load(_dir);

        Assert.Equal("http://0.0.0.0:8080", StartupConfig.WebAddress);
    }

    [Fact]
    public void Parses_Unquoted_Value_And_Ignores_Inline_Comment()
    {
        WriteConfig("web-address: http://localhost:9000  # 行内注释");
        StartupConfig.Load(_dir);

        Assert.Equal("http://localhost:9000", StartupConfig.WebAddress);
    }

    [Fact]
    public void Invalid_Url_Falls_Back_To_Default()
    {
        WriteConfig("web-address: \"not-a-url\"");
        StartupConfig.Load(_dir);

        Assert.Equal(StartupConfig.DefaultWebAddress, StartupConfig.WebAddress);
    }

    [Fact]
    public void Malformed_Yaml_Falls_Back_To_Default()
    {
        WriteConfig("web-address: [unclosed");
        StartupConfig.Load(_dir);

        Assert.Equal(StartupConfig.DefaultWebAddress, StartupConfig.WebAddress);
    }

    [Fact]
    public void Unknown_Keys_Are_Ignored()
    {
        WriteConfig("""
            foo: bar
            web-address: "http://localhost:7000"
            """);
        StartupConfig.Load(_dir);

        Assert.Equal("http://localhost:7000", StartupConfig.WebAddress);
    }

    [Fact]
    public void Reload_Resets_To_Default_When_File_Removed()
    {
        WriteConfig("web-address: \"http://localhost:7000\"");
        StartupConfig.Load(_dir);
        Assert.Equal("http://localhost:7000", StartupConfig.WebAddress);

        File.Delete(Path.Combine(_dir, StartupConfig.FileName));
        StartupConfig.Load(_dir);

        Assert.Equal(StartupConfig.DefaultWebAddress, StartupConfig.WebAddress);
    }
}
