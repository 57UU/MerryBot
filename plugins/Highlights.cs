using NapcatClient;
using NapcatClient.MessageType;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZhipuClient;

namespace BotPlugin;

[PluginTag("highlights", "Highlights", "群刊插件/highlights status/flush", priority: 1001, type: PluginType.Interactive, isIgnore: false)]
public class Highlights : Plugin
{
    private readonly AiMessage aiMessage;
    private ZhipuAi aiClient;
    private int count;
    private HighlightsData storageData = new();
    private StorageManagerPlugin storageManager;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> groupLocks = new();

    public Highlights(PluginInterop interop, AiMessage aiMessage, StorageManagerPlugin storageManager) : base(interop)
    {
        this.aiMessage = aiMessage;
        this.storageManager = storageManager;
        string prompt = interop.GetVariableOrSetDefault("highlights-prompt", "你是一个专业的群刊编辑，负责编辑群刊的高亮内容。");
        count = interop.GetIntVariableOrSetDefault("message-count", 500);
        aiClient = new(aiMessage.defaultToken, prompt, aiMessage.defaultModel, useBuildinTools: false);
        aiClient.RegisterBingSearch();
        aiClient.RegisterBrowser();
        RegisterMarkdownTool();
    }
    private void RegisterMarkdownTool()
    {
        var mdSender = new ToolDef();
        mdSender.Function.Name = "send_markdown";
        mdSender.Function.Description = "支持mermaid、latex公式";
        mdSender.HideOutputOnInvoking = true;
        mdSender.DynamicPrompt = "当你完成信息检阅，有足够丰富的思路后，请使用send_markdown工具发送群刊。";
        mdSender.Function.Parameters.AddRequired("md", new ParameterProperty() { Type = "string", Description = "需要发送的Markdown文本" });
        mdSender.Function.FunctionCall = async (parameters) =>
        {
            var markdown = parameters["md"];
            byte[] img = await ZhipuAi.browser.TakeMarkdownScreenshot(markdown.GetString()!);
            await Actions.SendGroupMessage(parameters.SpecialTag, [ImageData.FromBinary(img)]);
            return "done";
        };
        mdSender.Behavior = ToolBehavior.ExitAfterUse;
        aiClient.RegisterTool(mdSender);
    }

    public override async Task OnLoaded()
    {
        storageData = await Interop.PluginStorage.Load<HighlightsData>() ?? new HighlightsData();
        Logger.Info($"Highlights plugin loaded. Target count: {count}");
    }

    public override void OnGroupMessage(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (!storageData.GroupMessageCount.ContainsKey(groupId))
        {
            storageData.GroupMessageCount[groupId] = 0;
        }

        storageData.GroupMessageCount[groupId]++;

        if (storageData.GroupMessageCount[groupId] >= count)
        {
            Logger.Info($"Group {groupId} reached {count} messages. Generating highlights...");
            storageData.GroupMessageCount[groupId] = 0;
            _ = GenerateHighlights(groupId);
        }

        // 异步保存计数，避免阻塞消息处理
        _ = Interop.PluginStorage.Save(storageData);
    }

    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (IsStartsWith(chain, "/highlights status"))
        {
            int current = storageData.GroupMessageCount.GetValueOrDefault(groupId, 0);
            _ = Actions.SendGroupMessage(groupId, $"当前群聊消息计数：{current}/{count}");
        }
        else if (IsStartsWith(chain, "/highlights flush"))
        {
            _ = Actions.SendGroupMessage(groupId, $"正在编写群刊...");
            storageData.GroupMessageCount[groupId] = 0;
            _ = Interop.PluginStorage.Save(storageData);
            _ = GenerateHighlights(groupId);
        }
    }
    async Task GenerateHighlights(long groupId){
        var groupLock = groupLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        if (!groupLock.Wait(0))
        {
            Logger.Info($"群 {groupId} 已有群刊生成任务在进行，跳过重复请求");
            _ = Actions.SendGroupMessage(groupId, "群刊生成任务已在进行中，不要着急哦");
            return;
        }
        try
        {
            await _generateHighlights(groupId);
        }
        finally
        {
            groupLock.Release();
        }
    }

    async Task _generateHighlights(long groupId)
    {
        try
        {
            // 获取最近 count 条消息
            var messages = await storageManager.GroupHistoryRecorder.GetMessagesByGroupIdAsync(groupId, count);
            messages.Reverse(); // 恢复时间顺序

            if (messages.Count == 0)
            {
                Logger.Warn($"群 {groupId} 没有可用的历史消息，无法生成群刊");
                return;
            }

            // 格式化消息内容
            // var context = string.Join("\n", messages.Select(m =>
            // {
            //     var timeStr = m.Time.ToString("yyyy-MM-dd HH:mm");
            //     var name = string.IsNullOrEmpty(m.SenderGroupNickname) ? m.SenderNickname : m.SenderGroupNickname;
            //     var content = string.Join("", m.Messages.Select(tm => tm.ToString()));
            //     return $"[{timeStr}] {name}: {content}";
            // }));
            StringBuilder sb=new();
            ResourceLimit limit=new(){
                ImageLimit=3,
            };
            foreach(var message in messages){
                var nickname=string.IsNullOrEmpty(message.SenderGroupNickname) ? message.SenderNickname : message.SenderGroupNickname;
                var timeStr=message.Time.ToString("yyyy-MM-dd HH:mm");
                sb.Append($"{timeStr}[user:{nickname}]");
                sb.AppendLine(await aiMessage.ExtractMessage(message.Messages, groupId,recursive:false,resourceLimit:limit));
            }


            aiClient.AddHistory(groupId, new ZhipuMessage { Role = ZhipuAi.USER, Content = $"以下是群聊消息内容：\n{sb}" });

            bool useFallback = false;
            const string instruction = "请根据以上提供的群聊消息生成一份有趣的群刊，并使用 send_markdown 工具发送。";
            string resultText = "";

            try
            {
                await foreach (var chunk in aiClient.Ask(instruction, groupId, "", groupId))
                {
                    resultText = chunk;
                }
                // 如果 Ask 正常结束且未触发 ExitAfterUseException，说明 AI 可能没有按预期使用工具
                useFallback = true;
            }
            catch (ExitAfterUseException ex)
            {
                Logger.Info($"群 {groupId} 生成群刊时使用了工具 {ex.ToolName}，流程正常结束");
                return; // 工具已经发送了图片，直接退出
            }

            // 回退逻辑：如果 AI 没有调用工具，则直接发送文本结果
            if (useFallback && !string.IsNullOrWhiteSpace(resultText))
            {
                Logger.Warn($"群 {groupId} 的 AI 未使用 send_markdown 工具，改为直接发送文本内容");
                await Actions.SendGroupMessage(groupId, resultText);
            }
        }
        catch (NotAvailableException)
        {
            _ = Actions.SendGroupMessage(groupId,"正在生成中，不要着急哦");
        }
        catch (Exception ex)
        {
            Logger.Error($"生成群刊失败: {ex.Message}");
            _ = Actions.SendGroupMessage(groupId, $"生成群刊失败: {ex.Message}");
        }
        finally
        {
            aiClient.Reset(groupId);
        }
    }
}

public class HighlightsData
{
    public Dictionary<long, int> GroupMessageCount { get; set; } = new();
}
