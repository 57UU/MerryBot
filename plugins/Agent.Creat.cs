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
        // 记忆工具集由 memoryService 实例化：内部完成懒创建空 index 记录并注入记忆上下文
        var memoryToolSet = await memoryService.CreateMemoryToolSetAsync(sessionId);

        // ── 辅助视觉模型：主模型不具备视觉能力时用于看图，支持多个并逐层降级 ────────
        var visionClients = new List<Client>();
        var visionModelIds = agentConfig.VisionLlmModels ?? [];
        foreach (var modelId in visionModelIds)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }
            try
            {
                visionClients.Add((await llmProvider.CreateClientAsync(modelId)).Client);
            }
            catch (Exception e)
            {
                Logger.Warn($"辅助视觉模型不可用（{modelId}），将跳过：{e.Message}");
            }
        }
        var visionRouter = new VisionRouter(
            resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput),
            visionClients,
            agentConfig.VisionPrompt);
        Logger.Info($"Agent 视觉能力: 主模型={(resolved.Model.Capabilities.HasFlag(LlmModelCapabilities.ImageInput) ? "有" : "无")}"
            + (visionClients.Count == 0
                ? "，未配置可用的辅助视觉模型"
                : $"，辅助视觉模型={string.Join(", ", visionModelIds.Where(id => !string.IsNullOrWhiteSpace(id)))}"));

        var dynamicPrompt = $"你当前正在{sessionKey.Platform}平台，类型为{sessionKey.ChannelType}的channel中聊天";
        // bash 工具门禁：AllowShell 默认关闭，未开启时不注册 TerminalToolSet（模型无法执行 shell）
        var tools = new List<ToolSet>
        {
            new MessageTool(Interop.MessageService, Channel, browser, sessionKey, visionRouter, agentConfig.MaxImageSizeMb * 1024 * 1024),
            new TodoListToolSet(),
            new WebTools(browser),
            new PromptToolSet(dynamicPrompt),
            skillToolSet,
            new Cron(sessionId, Interop.ClockService),
            memoryToolSet,
        };
        if (agentConfig.AllowShell)
        {
            tools.Add(new TerminalToolSet(sessionManager, sessionId, user: agentConfig.ShellUser, visionRouter: visionRouter, maxImageBytes: agentConfig.MaxImageSizeMb * 1024 * 1024));
        }

        var agentOptions = new AgentOptions
        {
            SystemPrompt = agentConfig.AiPrompt,
            MaxOutputTokens = resolved.Model.MaxOutputTokens,
            MaxIterations = Math.Clamp(agentConfig.MaxIterations, 1, 150),
            ContextCompactRatio = Math.Clamp(agentConfig.ContextCompactRatio, 0.1, 0.9),
            // 思维强度跟随模型配置（ModelRecord.ReasoningEffort），换模型自动跟随
            ReasoningEffort = resolved.Model.ReasoningEffort,
            // 审计记录：每条会话消息（user/assistant/tool）落库 ai_messages，仅文本、不受上下文压缩影响
            OnMessageRecorded = (message, usage) => RecordAiAuditMessageAsync(sessionId, message, usage),
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
