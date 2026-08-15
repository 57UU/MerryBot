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
        // Channel 内部已捕获异常并记录日志（含插件 id），不会抛出
        Action<string> sendMessage = (msg) => _ = Channel.SendMessage(sessionKey, msg);
        await persistenceStartTask;

        var resolved = await llmProvider.CreateClientAsync(agentConfig.LlmModel);
        var skillToolSet = await SkillToolSet.CreateAsync(skillService);
        var memoryPromptInjection = await memoryService.GetPromptInjectionAsync(sessionId);

        // ── 辅助视觉模型：主模型不具备视觉能力时用于看图 ─────────────────────
        Client? visionClient = null;
        if (!string.IsNullOrWhiteSpace(agentConfig.VisionLlmModel))
        {
            try
            {
                visionClient = (await llmProvider.CreateClientAsync(agentConfig.VisionLlmModel)).Client;
            }
            catch (Exception e)
            {
                Logger.Warn($"辅助视觉模型不可用（VisionLlmModel={agentConfig.VisionLlmModel}），图片将无法查看：{e.Message}");
            }
        }
        var visionRouter = new VisionRouter(
            resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput),
            visionClient,
            agentConfig.VisionPrompt);
        Logger.Info($"Agent 视觉能力: 主模型={(resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput) ? "有" : "无")}"
            + (string.IsNullOrWhiteSpace(agentConfig.VisionLlmModel) ? "，未配置辅助视觉模型" : $"，辅助视觉模型={agentConfig.VisionLlmModel}"));

        // bash 工具门禁：AllowShell 默认关闭，未开启时不注册 TerminalToolSet（模型无法执行 shell）
        var tools = new List<ToolSet>
        {
            new MessageTool(Interop.MessageService, Channel, browser, sessionKey, visionRouter, agentConfig.MaxImageSizeMb * 1024 * 1024),
            new TodoListToolSet(),
            new WebTools(browser),
            skillToolSet,
            new Cron(sessionId, Interop.ClockService),
            new MemoryToolSet(memoryService, sessionId, memoryPromptInjection),
        };
        if (agentConfig.AllowShell)
        {
            tools.Add(new TerminalToolSet(sessionManager, sessionId, visionRouter: visionRouter, maxImageBytes: agentConfig.MaxImageSizeMb * 1024 * 1024));
        }

        var agentOptions = new AgentOptions
        {
            SystemPrompt = agentConfig.AiPrompt,
            MaxOutputTokens = resolved.Model.MaxOutputTokens,
            MaxIterations = Math.Clamp(agentConfig.MaxIterations, 1, 20),
            ContextCompactRatio = Math.Clamp(agentConfig.ContextCompactRatio, 0.1, 0.95),
            // 思维强度跟随模型配置（ModelRecord.ReasoningEffort），换模型自动跟随
            ReasoningEffort = resolved.Model.ReasoningEffort,
        };
        // 子任务工具：复用父会话同一模型客户端、options 与工具列表（含自身，允许嵌套派生子任务）；
        // 完成/失败时以 stackable 消息注入本会话，主 Agent 拿到结果后继续处理
        tools.Add(new SubAgentToolSet(
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            agentOptions,
            tools,
            async msg =>
            {
                var session = await sessionManager.GetSessionAsync(sessionId);
                await session.Chat(msg, type: "subagent_result", stackable: true);
            },
            disposeCts.Token));

        var agent = await Agent.Agent.Create(
            new DatabaseContextHistory(Interop.PluginStorage.PluginDatabaseScope, sessionId),
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            agentOptions,
            tools);
        return (agent, sendMessage);
    }
}
