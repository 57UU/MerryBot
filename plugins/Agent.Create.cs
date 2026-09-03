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

        // 主模型与辅助视觉模型传入同一 sessionId 作 OpenCode 会话亲和 key：
        // 同一会话的主回合/压缩/视觉请求共享同一 x-opencode-session，后端亲和保持 prompt cache 温热
        var resolved = await llmProvider.CreateClientAsync(agentConfig.LlmModel, cancellationToken: default, sessionKey: sessionId);
        var skillToolSet = await SkillToolSet.CreateAsync(skillService);
        // 记忆工具集由 memoryService 实例化：内部完成懒创建空 index 记录并注入记忆上下文
        var memoryToolSet = await memoryService.CreateMemoryToolSetAsync(sessionId);
        // 按群提示词 override：命中则完全替换全局 AiPrompt；为空回退全局。
        // 快照在会话创建时确定，改完需 /new（或重启/空闲重建）才对该群生效
        string systemPrompt = ResolveSystemPrompt(agentConfig.AiPrompt, await promptOverrideService.GetOverrideAsync(sessionId));
        if (!string.Equals(systemPrompt, agentConfig.AiPrompt, StringComparison.Ordinal))
        {
            Logger.Info($"会话 {sessionId} 命中提示词 override，使用该群专属提示词。");
        }

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
                visionClients.Add((await llmProvider.CreateClientAsync(modelId, cancellationToken: default, sessionKey: sessionId)).Client);
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

        var dynamicPrompt = $"你当前正在{sessionKey.Platform}平台，类型为{sessionKey.ChannelType}的channel中聊天，你看到的消息格式为 [用户 id(昵称:nickname)] 消息内容";
        // 自动水群模式判定收敛在 MessageTool 构造参数：启用才传入快照（注册 send_message），
        // 否则传 null，工具集与原有行为一致；快照在会话创建时确定，开关模式后需 /new 重建会话
        AutoChatSettings? autoChatSettings = agentConfig.AutoChatEnable
            ? new AutoChatSettings
            {
                DryRun = agentConfig.AutoChatDryRun,
                Budget = autoChatBudgets.GetOrAdd(sessionId, static _ => new AutoChatSendBudget()),
            }
            : null;
        if (autoChatSettings != null)
        {
            dynamicPrompt += "\n自动水群模式已启用：旁观消息没有 @ 你，只有感兴趣、有话想说时才调用 send_message 发送；不感兴趣时直接返回空字符串。在被 @ 的对话轮次中不要调用 send_message（最终回复会自动发送，混用会导致重复发送）。";
        }
        // bash 工具门禁：AllowShell 默认关闭，未开启时不注册 TerminalToolSet（模型无法执行 shell）
        var tools = new List<ToolSet>
        {
            new MessageTool(Interop.MessageService, Channel, browser, sessionKey, visionRouter, agentConfig.MaxImageSizeMb * 1024 * 1024, Logger, autoChatSettings),
            new TodoListToolSet(),
            new WebTools(browser),
            new PromptToolSet(dynamicPrompt),
            skillToolSet,
            new Cron(sessionId, Interop.Clock),
            memoryToolSet,
        };
        if (agentConfig.AllowShell)
        {
            tools.Add(
                new TerminalToolSet(
                    sessionManager,
                    sessionId,
                    user: agentConfig.ShellUser,
                    visionRouter: visionRouter,
                    maxImageBytes: agentConfig.MaxImageSizeMb * 1024 * 1024,
                    maxBackgroundTasks: Math.Clamp(agentConfig.MaxBackgroundTasks, 1, 64)
                    )
                );
        }

        var agentOptions = new AgentOptions
        {
            SystemPrompt = systemPrompt,
            MaxOutputTokens = resolved.Model.MaxOutputTokens,
            MaxIterations = Math.Clamp(agentConfig.MaxIterations, 1, 150),
            MaxConcurrentToolCalls = Math.Clamp(agentConfig.MaxConcurrentToolCalls, 1, 64),
            ContextCompactRatio = Math.Clamp(agentConfig.ContextCompactRatio, 0.1, 0.9),
            // 思维强度跟随模型配置（ModelRecord.ReasoningEffort），换模型自动跟随
            ReasoningEffort = resolved.Model.ReasoningEffort,
            // 审计记录：每条会话消息（user/assistant/tool）落库 ai_messages，仅文本、不受上下文压缩影响
            OnMessageRecorded = (message, usage) => RecordAiAuditMessageAsync(sessionId, message, usage),
            // 运行事件（会话/工具调用/压缩/流式重置）桥接到插件日志，WebUI 日志页可见
            OnLog = e => AgentLogBridge.Log(e, Logger),
        };
        // 子任务工具：复用父会话同一模型客户端、options 与工具列表（不含自身，不允许嵌套派生子任务）；
        // 完成/失败时经 EnqueueStackable 把结果块注入本会话队列（同 type 合并），主 Agent 拿到结果后继续处理；
        // 模型用 subagent_output 拉取全文后，withdraw 撤销已入队块，避免推送与拉取双通道重复投递
        tools.Add(new SubAgentToolSet(
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            agentOptions,
            [.. tools],// 不包含自身，不允许嵌套派生子任务
            async (taskId, msg) =>
            {
                var session = await sessionManager.GetSessionAsync(sessionId);
                session.EnqueueStackable("tool_result", taskId, msg,
                    () => new StackableMessage(null, CancellationToken.None, null));
            },
            async taskId =>
            {
                var session = await sessionManager.GetSessionAsync(sessionId);
                session.RemoveQueued("tool_result", taskId);
            },
            disposeCts.Token,
            maxSubagents: Math.Clamp(agentConfig.MaxSubagents, 1, 64)));

        var agent = await Agent.Agent.Create(
            new DatabaseContextHistory(Interop.PluginStorage.PluginDatabaseScope, sessionId),
            resolved.Client,
            Math.Max(resolved.Model.ContextLength, 1024),
            agentOptions,
            tools);
        return (agent, sendMessage);
    }

    /// <summary>
    /// 提示词回退语义（纯函数）：override 非空则完全替换全局提示词，否则回退全局。
    /// 抽出以便单测覆盖，不依赖数据库。
    /// </summary>
    internal static string ResolveSystemPrompt(string globalPrompt, string? groupOverride) =>
        string.IsNullOrWhiteSpace(groupOverride) ? globalPrompt : groupOverride;
}
