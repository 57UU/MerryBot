using Agent;
using Agent.Session;
using Agent.Tools;
using LlmBackend;
using LlmClient;

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

        // ── Agent 可配置项(均可在群聊中通过配置命令调整) ──────────────────────
        var modelId = Interop.GetVariableOrSetDefault("llm-model", "opencode-go/deepseek-v4-flash");
        var prompt = Interop.GetVariableOrSetDefault("ai-prompt", "你是一个乐于助人、回答简洁的群聊助手。");
        var maxIterations = Interop.GetIntVariableOrSetDefault("max-iterations", 20);
        var contextCompactRatio = Interop.GetStructVariableOrSetDefault("context-compact-ratio", 0.7);
        var visionModelId = Interop.GetVariableOrSetDefault("vision-llm", string.Empty);
        var visionPrompt = Interop.GetVariableOrSetDefault("vision-prompt", "请详细描述这张图片的内容。");

        var resolved = await llmProvider.CreateClientAsync(modelId);

        // ── 辅助视觉模型：主模型不具备视觉能力时用于看图 ─────────────────────
        Client? visionClient = null;
        if (!string.IsNullOrWhiteSpace(visionModelId))
        {
            try
            {
                visionClient = (await llmProvider.CreateClientAsync(visionModelId)).Client;
            }
            catch (Exception e)
            {
                Logger.Warn($"辅助视觉模型不可用（vision-llm={visionModelId}），图片将无法查看：{e.Message}");
            }
        }
        var visionRouter = new VisionRouter(
            resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput),
            visionClient,
            visionPrompt);
        Logger.Info($"Agent 视觉能力: 主模型={(resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput) ? "有" : "无")}"
            + (string.IsNullOrWhiteSpace(visionModelId) ? "，未配置辅助视觉模型" : $"，辅助视觉模型={visionModelId}"));

        var agent = await Agent.Agent.Create(
            new DatabaseContextHistory(Interop.PluginDatabase, sessionId),
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            new AgentOptions
            {
                SystemPrompt = prompt,
                MaxOutputTokens = resolved.Model.MaxOutputTokens,
                MaxIterations = Math.Clamp(maxIterations, 1, 100),
                ContextCompactRatio = Math.Clamp(contextCompactRatio, 0.1, 0.95),
                // 思维强度跟随模型配置（ModelRecord.ReasoningEffort），换模型自动跟随
                ReasoningEffort = resolved.Model.ReasoningEffort,
            },
            [
                new MessageTool(Interop.MessageService, Bot, browser, long.Parse(sessionKey.Id), visionRouter),
                new TodoListToolSet(),
                new WebTools(browser),
                new SkillToolSet(skillsPath),
                new TerminalToolSet(sessionManager, sessionId, visionRouter: visionRouter),
                new Cron(sessionId, clockService),
            ]);
        return (agent, sendMessage);
    }
}
