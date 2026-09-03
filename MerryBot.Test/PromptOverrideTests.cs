using BotPlugin;

namespace MerryBot.Test;

/// <summary>
/// 按群提示词 override 回退语义测试：空/空白回退全局，非空完全替换。
/// 纯函数覆盖，不依赖数据库与网络。
/// </summary>
public sealed class PromptOverrideTests
{
    [Fact]
    public void Null_Override_Falls_Back_To_Global()
    {
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt("全局", null));
    }

    [Fact]
    public void Empty_Override_Falls_Back_To_Global()
    {
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt("全局", string.Empty));
    }

    [Fact]
    public void Whitespace_Override_Falls_Back_To_Global()
    {
        Assert.Equal("全局", AgentPlugin.ResolveSystemPrompt("全局", "  \n "));
    }

    [Fact]
    public void NonEmpty_Override_Replaces_Global()
    {
        Assert.Equal("群专属", AgentPlugin.ResolveSystemPrompt("全局", "群专属"));
    }
}
