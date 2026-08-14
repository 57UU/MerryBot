using Agent;
using Agent.Session;
using Agent.Tools;

namespace BotPlugin;

public partial class AgentPlugin : Plugin
{
    private async Task<(Agent.Agent, Action<string>)> CreateAgent(string sessionId)
    {
        var sessionKey = SessionKey.Parse(sessionId);
        if (sessionKey.Platform != "qq" || sessionKey.ChannelType != "group")
        {
            throw new ArgumentException("Invalid session key format.");
        }
        Action<string> sendMessage = (msg) =>
        {
            _ = Bot.SendGroupMessage(long.Parse(sessionKey.Id), msg);
        };
        await clockServiceStartTask;
        var resolved = await llmProvider.CreateClientAsync(
            Interop.GetVariableOrSetDefault("llm-model", "opencode-go/deepseek-v4-flash"));
        var prompt = Interop.GetVariableOrSetDefault("ai-prompt", "你是一个乐于助人、回答简洁的群聊助手。");
        var agent = await Agent.Agent.Create(
            new DatabaseContextHistory(Interop.PluginDatabase, sessionId),
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            new AgentOptions
            {
                SystemPrompt = prompt,
                MaxOutputTokens = resolved.Model.MaxOutputTokens,
            },
            [
                new MessageTool(Interop.MessageService, Bot, browser, long.Parse(sessionKey.Id)),
                new TodoListToolSet(),
                new WebTools(browser),
                new SkillToolSet(skillsPath),
                new TerminalToolSet(sessionManager, sessionId),
                new Cron(sessionId, clockService),
            ]);
        return (agent, sendMessage);
    }
}
