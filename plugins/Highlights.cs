using NapcatClient;
using NapcatClient.MessageType;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        float temperature = interop.GetStructVariable<float>("temperature") ?? 1.3f;
        var model = ModelPreset.DeepSeekReasoner.With(temperature: temperature);
        aiClient = new(aiMessage.GetToken(model)!, prompt, model, useBuildinTools: false);
        aiClient.Logger=Logger;
        aiClient.RegisterBingSearch();
        aiClient.RegisterBrowser();
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
            if (IsGeneratingHighlights(groupId))
            {
                _ = Actions.SendGroupMessage(groupId, "群刊生成任务已在进行中，不要着急哦");
                return;
            }
            _ = Actions.SendGroupMessage(groupId, $"正在编写群刊...");
            storageData.GroupMessageCount[groupId] = 0;
            _ = Interop.PluginStorage.Save(storageData);
            _ = GenerateHighlights(groupId);
        }
    }
    bool IsGeneratingHighlights(long groupId)
    {
        return groupLocks.TryGetValue(groupId, out var groupLock) && groupLock.CurrentCount == 0;
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
            var limit = new ResourceLimit { ImageLimit = 3, ImageInterpreterType = ImageInterpreterType.Quick };
            var extractTasks = messages.Select(async (message, index) =>
            {
                var nickname=string.IsNullOrEmpty(message.SenderGroupNickname) ? message.SenderNickname : message.SenderGroupNickname;
                var timeStr=message.Time.ToString("yyyy-MM-dd HH:mm");
                var extracted = await aiMessage.ExtractMessage(message.Messages, groupId, recursive:false, resourceLimit:limit);
                return new { time = message.Time, index, line = $"{timeStr}[user:{nickname}]{extracted}\n" };
            });
            var extractedLines = await Task.WhenAll(extractTasks);
            foreach (var item in extractedLines.OrderBy(i => i.time).ThenBy(i => i.index))
            {
                sb.Append(item.line);
            }


            aiClient.AddHistory(groupId, new ZhipuMessage { Role = ZhipuAi.USER, Content = $"以下是需要分析的群聊消息内容：\n{sb}" });

            // 第一步：生成目录 (TOC)
            const string tocInstruction = "请基于以上提供的群聊消息生成一份有趣的群刊目录。目录应包含 3-5 个章节，每个章节简要描述要点。请以 JSON 数组格式返回，只返回 JSON 数组本身，例如：[\"章节1标题: 简要描述\", \"章节2标题: 简要描述\"]";
            string tocJson = "";
            await foreach (var chunk in aiClient.Ask(tocInstruction, groupId, "", groupId))
            {
                tocJson = chunk;
            }

            // 处理可能包含 ```json ... ``` 的情况
            if (tocJson.Contains("```json"))
            {
                tocJson = tocJson.Split("```json")[1].Split("```")[0];
            }
            else if (tocJson.Contains("```"))
            {
                var split = tocJson.Split("```");
                if (split.Length >= 2)
                {
                    tocJson = split[1];
                }
            }
            tocJson = tocJson.Trim();

            List<string> toc = new();
            try
            {
                toc = JsonSerializer.Deserialize<List<string>>(tocJson) ?? new List<string>();
            }
            catch (Exception)
            {
                // Fallback: 如果 JSON 解析失败，尝试按行解析
                toc = tocJson.Split('\n', '\r')
                    .Select(s => s.Trim('-',' ','\r','1','2','3','4','5','.','*'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            if (toc.Count == 0)
            {
                Logger.Warn($"群 {groupId} 目录生成失败，尝试直接生成全文...");
                const string fallbackInstruction = "请根据以上提供的群聊消息生成一份完整的、有趣的群刊，要求使用 Markdown 语法排版。";
                string fullMarkdown = "";
                await foreach (var chunk in aiClient.Ask(fallbackInstruction, groupId, "", groupId))
                {
                    fullMarkdown = chunk;
                }
                if (!string.IsNullOrWhiteSpace(fullMarkdown))
                {
                    byte[] fallbackImg = await ZhipuAi.browser.TakeMarkdownScreenshot(fullMarkdown);
                    await Actions.SendGroupMessage(groupId, [ImageData.FromBinary(fallbackImg)]);
                }
                return;
            }

            // 第二步：逐章节生成内容
            StringBuilder finalMarkdown = new();
            finalMarkdown.AppendLine("# 群刊高亮内容\n");
            
            foreach (var item in toc)
            {
                string sectionContent = "";
                string sectionTitle = item.Split(':').First().Trim();
                string sectionInstruction = $"现在请为目录项 '{item}' 编写详细的群刊章节内容。要求风格幽默、戏剧性强，充分挖掘群聊中的梗，并适当使用 Markdown 语法进行排版。只需返回该章节的内容，不要包含标题、前言或总结。";
                
                await foreach (var chunk in aiClient.Ask(sectionInstruction, groupId, "", groupId))
                {
                    sectionContent = chunk;
                }
                finalMarkdown.AppendLine($"## {sectionTitle}");
                finalMarkdown.AppendLine(sectionContent);
                finalMarkdown.AppendLine("\n---\n");
            }

            // 第三步：最终渲染发送
            Logger.Info($"群 {groupId} 群刊内容生成完毕，正在进行最终渲染...");
            byte[] img = await ZhipuAi.browser.TakeMarkdownScreenshot(finalMarkdown.ToString());
            await Actions.SendGroupMessage(groupId, [ImageData.FromBinary(img)]);
        }
        catch (NotAvailableException)
        {
            _ = Actions.SendGroupMessage(groupId, "正在生成中，不要着急哦");
        }
        catch (ExitAfterUseException ex)
        {
            Logger.Info($"群 {groupId} 在回退或过程中使用了工具 {ex.ToolName}，流程已由工具接管并结束");
        }
        catch (Exception ex)
        {
            Logger.Error($"生成群刊失败: {ex.Message}\n{ex.StackTrace}");
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
