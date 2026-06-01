using BrowserService;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace OpenAiClient;

public partial class OpenAiCompatible : IDisposable
{
    string token;
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    public ModelPreset ModelPreset { get; private set; } = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。

    HttpClient client = new HttpClient();
    private HttpClient? _compressionClient;
    private List<ToolDef> Tools { get; set; } = new();
    private Dictionary<string, ToolDef> functionMapper = new();

    readonly string prompt;
    /// <summary>
    /// Browser 实例，由外部（如 AiMessage 插件）管理生命周期，通过构造函数注入
    /// </summary>
    public readonly Browser? browser;

    public OpenAiCompatible(string token, string prompt, ModelPreset modelPreset, HistoryRecorder? historyRecorder = null, bool useBuildinTools = true, Browser? browser = null)
    {
        this.token = token;
        this.prompt = prompt;
        this.browser = browser;
        this.HistoryRecorder = historyRecorder;
        SystemPrompt = new OpenAiMessage()
        {
            Role = SYSTEM,
            Content = prompt,
        };
        SetModelPreset(modelPreset, token);
        options.Converters.Add(new MessageConverter());
        //tools
        if (useBuildinTools)
        {
            AddBuiltInTools();
        }

    }
    public void SetModelPreset(ModelPreset modelPreset, string token)
    {
        client.DefaultRequestHeaders.Clear();
        // 创建HttpClient并设置请求头
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        this.ModelPreset = modelPreset;
    }
    /// <summary>
    /// register built-in tools
    /// </summary>
    private void AddBuiltInTools()
    {

        RegisterGetTime();

        // Browser 相关工具只在 browser 实例注入时注册
        if (browser != null)
        {
            RegisterBrowser();

            //RegisterWeiboHot();

            if (ModelPreset.enableSearch)
            {
                RegisterBingSearch();
            }
        }
    }
    /// <summary>
    /// register tool so that it can be called by assistant
    /// </summary>
    /// <param name="tool"></param>
    public void RegisterTool(ToolDef tool)
    {
        Tools.Add(tool);
        functionMapper.Add(tool.Function.Name, tool);
    }
    readonly ConcurrentDictionary<long, List<OpenAiMessage>> history = new();
    readonly ConcurrentDictionary<long, SemaphoreSlim> mutex = new();
    public ReadOnlySpan<OpenAiMessage> GetDialogHistory(long uid)
    {
        history.TryGetValue(uid, out var dialog);
        if (dialog == null)
        {
            return Span<OpenAiMessage>.Empty;
        }
        return CollectionsMarshal.AsSpan(dialog);
    }
    public void SetDialogHistory(long uid, IEnumerable<OpenAiMessage> messages)
    {
        history[uid] = messages.ToList();
    }
    public void AppendDialogHistory(long uid, IEnumerable<OpenAiMessage> messages)
    {
        var currentHistory = history.GetOrAdd(uid, _ => new List<OpenAiMessage>());
        currentHistory.AddRange(messages);
    }
    public string SystemPromptContent => SystemPrompt.Content!;
    SemaphoreSlim EnsureMutexExists(long groupId)
    {
        return mutex.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
    }
    private OpenAiMessage SystemPrompt;
    /// <summary>
    /// reset dialog for a group
    /// </summary>
    /// <param name="id"></param>
    public void Reset(long id)
    {
        var mutex = EnsureMutexExists(id);
        mutex.Wait();
        history.TryRemove(id, out _);
        mutex.Release();
    }
    public TimeSpan AutoNewSpan { get; set; } = TimeSpan.FromHours(12);

    public void AddHistory(long id, OpenAiMessage message)
    {
        var currentHistory = history.GetOrAdd(id, _ => new List<OpenAiMessage>());
        currentHistory.Add(message);
    }

    /// <summary>
    /// 处理请求
    /// </summary>
    /// <param name="content">询问内容</param>
    /// <param name="id">区分不同对话的id</param>
    /// <param name="sender">发送者</param>
    /// <param name="specialTag">一个tag，该tag会出现在function call的参数中</param>
    /// <returns>异步字符串迭代器，模型返回结果</returns>
    public async IAsyncEnumerable<string> Ask(string content, long id, string sender, long specialTag = 0)
    {
        var recorder = (OpenAiMessage message) => HistoryRecorder?.Invoke(id, message.Role, message.Content);
        var mutex = EnsureMutexExists(id);
        if (!mutex.Wait(0))
        {
            throw new NotAvailableException("上一个请求尚未完成");
        }
        try
        {
            bool done = false;
            //if last message is too old, start a new conversation
            if (history.TryGetValue(id, out List<OpenAiMessage>? value))
            {
                var lastMessage = value.LastOrDefault();
                if (lastMessage != null)
                {
                    if (DateTime.Now - lastMessage.time > AutoNewSpan)
                    {
                        history.TryRemove(id, out _);
                    }
                }
            }

            if (!history.TryGetValue(id, out List<OpenAiMessage>? currentHistory))
            {
                //if currentHistory is null, create a new one
                currentHistory = new List<OpenAiMessage>();
                history.TryAdd(id, currentHistory);
            }

            if (currentHistory.Count == 0 || currentHistory[0].Role != SYSTEM)
            {
                OpenAiMessage prompt;
                if (UseDynamicPrompt)
                {
                    StringBuilder sb = new(SystemPrompt.Content);
                    sb.AppendLine($"\n这段对话的开始时间是{DateTime.Now.ToString("yyyy-MM-dd HH:mm")}");
                    var usableTools = await GetUsableToolsByTag(specialTag);
                    foreach (var tool in usableTools)
                    {
                        string? dynamicContent = tool.DynamicPromptFunc != null
                            ? await tool.DynamicPromptFunc(specialTag)
                            : tool.DynamicPrompt;
                        if (!string.IsNullOrWhiteSpace(dynamicContent))
                        {
                            sb.AppendLine(dynamicContent);
                        }
                    }
                    prompt = new()
                    {
                        Role = SystemPrompt.Role,
                        Content = sb.ToString(),
                    };
                }
                else
                {
                    prompt = SystemPrompt;
                }
                currentHistory.Insert(0, prompt);
                recorder(prompt);
            }

            var userQuery = new OpenAiMessage()
            {
                Role = USER,
                Content = $"{sender}{content}"
            };
            currentHistory.Add(userQuery);
            recorder(userQuery);
            // 上下文管理：自动压缩或滑动窗口
            int estimatedTokens = EstimateTokens(currentHistory, 0, currentHistory.Count);
            if (estimatedTokens > CompressTokenThreshold)
            {
                if (AutoCompressEnabled)
                {
                    int compressFrom = 1;
                    // 保留最近一半阈值的 token，压缩其余部分
                    int keepCharBudget = CompressTokenThreshold; // 字符数 = token * 2
                    int keepCharCount = 0;
                    int cutTarget = currentHistory.Count;
                    for (int i = currentHistory.Count - 1; i > compressFrom; i--)
                    {
                        keepCharCount += (currentHistory[i].Content ?? string.Empty).Length;
                        if (keepCharCount >= keepCharBudget)
                        {
                            cutTarget = i;
                            break;
                        }
                    }
                    int safeCutIndex = FindSafeCutIndex(currentHistory, cutTarget);
                    if (safeCutIndex > compressFrom)
                    {
                        var messagesToCompress = currentHistory.GetRange(compressFrom, safeCutIndex - compressFrom);
                        try
                        {
                            Logger.Info($"auto-compress: {messagesToCompress.Count} messages (~{estimatedTokens} tokens) -> summarizing...");
                            string summary = await CompressHistoryAsync(messagesToCompress);
                            var summaryMessage = new OpenAiMessage
                            {
                                Role = SYSTEM,
                                Content = $"[对话历史摘要]\n{summary}"
                            };
                            currentHistory.RemoveRange(compressFrom, safeCutIndex - compressFrom);
                            currentHistory.Insert(compressFrom, summaryMessage);
                            recorder(summaryMessage);
                            Logger.Info($"auto-compress: done, history now {currentHistory.Count} messages");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"auto-compress failed, falling back to deletion: {ex.Message}");
                            int safeCut = FindSafeCutIndex(currentHistory, 1 + (currentHistory.Count - SlidingWindowContext));
                            if (safeCut > 1)
                                currentHistory.RemoveRange(1, safeCut - 1);
                        }
                    }
                }
                else
                {
                    // Legacy 滑动窗口：按消息数删除
                    int excessCount = currentHistory.Count - SlidingWindowContext;
                    if (excessCount > 0)
                    {
                        int safeCutIndex = FindSafeCutIndex(currentHistory, 1 + excessCount);
                        currentHistory.RemoveRange(1, safeCutIndex - 1);
                    }
                }
            }
            while (!done)
            {
                string response;
                bool hideOutput = false;
                try
                {
                    var aiResponse = await Request(currentHistory, specialTag);
                    // 防御：API 在工具调用回合可能返回空 choices / 空 message。
                    if (aiResponse?.Choices == null || aiResponse.Choices.Count == 0)
                    {
                        Logger.Warn("OpenAiCompatible: empty choices in response");
                        response = "";
                        done = true;
                    }
                    else
                    {
                    var msg = aiResponse.Choices[0].Message;
                    // 防御：API 偶尔返回 choices[0] 为 null（理论上不应该发生但 System.Text.Json 不强制）
                    if (msg == null)
                    {
                        Logger.Warn("OpenAiCompatible: null message in choices[0]");
                        response = "";
                        done = true;
                    }
                    else
                    {
                    // 防御：工具调用回合（finish_reason == tool_calls）时，provider 经常只回 tool_calls，content 为 null。
                    // 直接 .StartsWith 会在第二轮 NRE。这里用空字符串兜底。
                    var rawContent = msg.Content ?? string.Empty;
                    if (rawContent.StartsWith("<think>"))
                    {
                        var cotEndIndex = rawContent.IndexOf("</think>");
                        if (cotEndIndex >= 0)
                        {
                            rawContent = rawContent.Substring(cotEndIndex + 8).TrimStart('\n', ' ');
                        }
                    }
                    response = rawContent;
                    if (aiResponse.Choices[0].FinishReason == TOOL_CALL)
                    {
                        ExitAfterUseException? exitMessage = null;
                        var assistantMessage = new AssistantMessage()
                        {
                            Role = msg.Role,
                            Content = rawContent,
                            ReasoningContent = msg.ReasoningContent,
                            ToolCalls = new()
                        };
                        // 防御：ToolCalls 在某些响应里可能为 null
                        var toolCalls = msg.ToolCalls ?? new List<ToolCall>();
                        foreach (var i in toolCalls)
                        {
                            // 防御：i.Function 在异常响应里可能为 null
                            if (i?.Function == null) continue;
                            assistantMessage.ToolCalls.Add(new ToolCallSubMessage()
                            {
                                Id = i.Id,
                                Function = i.Function
                            });
                            if (!string.IsNullOrEmpty(i.Function.Name) && functionMapper.TryGetValue(i.Function.Name, out var toolDef))
                            {
                                if (toolDef.HideOutputOnInvoking)
                                {
                                    hideOutput = true;
                                }
                                if (toolDef.Behavior == ToolBehavior.ExitAfterUse)
                                {
                                    exitMessage = new ExitAfterUseException($"after using {i.Function.Name}, conversation will be stopped", i.Function.Name);
                                }
                            }

                        }
                        currentHistory.Add(assistantMessage);
                        var toolCallsStr = string.Join(",", assistantMessage.ToolCalls.Select(i => $"{i.Function?.Name}({i.Function?.Arguments})"));
                        HistoryRecorder?.Invoke(id, assistantMessage.Role,
                            string.IsNullOrWhiteSpace(assistantMessage.Content) ? toolCallsStr : $"{assistantMessage.Content}:{toolCallsStr}");
                        //tool call
                        List<Task<ToolMessage>> tasks = new();
                        foreach (var f in toolCalls)
                        {
                            if (f?.Function == null) continue;
                            tasks.Add(HandleFunctionCall(f.Function, f.Id, specialTag));
                        }
                        await Task.WhenAll(tasks);
                        foreach (var i in tasks)
                        {
                            currentHistory.Add(i.Result);
                            recorder(i.Result);
                        }
                        if (exitMessage != null)
                        {
                            throw exitMessage;
                        }
                    }
                    else
                    {
                        var currentMessage = new AssistantMessage()
                        {
                            Role = msg.Role,
                            Content = rawContent,
                            ReasoningContent = msg.ReasoningContent
                        };
                        currentHistory.Add(currentMessage);
                        recorder(currentMessage);
                        done = true;
                    }
                    } // 防御 msg==null else
                    } // 防御 choices empty else
                }
                catch (ExitAfterUseException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Logger.Error($"OpenAiCompatible error: {e}");
                    response = "Error: " + e.Message;
                    done = true;
                }

                if (!hideOutput && !string.IsNullOrEmpty(response))
                {
                    yield return response.Trim();
                }
            }
        }
        finally
        {
            mutex.Release();
        }


    }
    private async Task<ToolMessage> HandleFunctionCall(Function func, string id, long specialTag)
    {
        ToolMessage message = new();
        message.Role = TOOL;
        message.Id = id;
        functionMapper.TryGetValue(func.Name, out var tool);
        Logger.Info($"FuncCall:{func.Name} {func.Arguments}");
        if (tool != null)
        {
            try
            {
                var args = JsonSerializer.Deserialize<FunctionCallArguments>(func.Arguments)
                    ?? throw new Exception("参数格式错误");
                args.SpecialTag = specialTag;
                message.Content = await tool.Function.FunctionCall.Invoke(args);
                Logger.Info("function result:" + message.Content);
            }
            catch (Exception e)
            {
                message.Content = "调用失败: " + e.Message;
                Logger.Warn("function error:" + e.Message);
            }
        }
        else
        {
            message.Content = "Error: " + func.Name + " not found";
            Logger.Warn("function not found:" + func.Name);
        }

        return message;
    }
    Dictionary<long, List<ToolDef>> _usableToolsCache = new();
    internal async Task<List<ToolDef>> GetUsableToolsByTag(long tag)
    {
        if (_usableToolsCache.TryGetValue(tag, out var cache))
        {
            return cache;
        }
        List<ToolDef> usableFunctionCall = new();
        var tasks = Tools.Select(tool => tool.isUseable(tag));
        await Task.WhenAll(tasks);
        foreach (var (tool, result) in Tools.Zip(tasks))
        {
            if (result.Result)
            {
                usableFunctionCall.Add(tool);
            }
        }
        _usableToolsCache.Add(tag, usableFunctionCall);
        return usableFunctionCall;

    }
    public async Task<ApiResponse> Request(IEnumerable<OpenAiMessage> messages, long specialTag)
    {
        var usableFunctionCall = await GetUsableToolsByTag(specialTag);
        var requestData = new Dictionary<String, object> {
            {"model",ModelPreset.model},
            {"messages",messages },
            {"tools",usableFunctionCall},
        };
        if (ModelPreset.temperature != null)
        {
            requestData.Add("temperature", ModelPreset.temperature);
        }
        requestData = requestData.Concat(ModelPreset.extraBody).ToDictionary();

        string jsonData = JsonSerializer.Serialize(requestData, options);

        string responseBody = await SendRequestAsync(client, ModelPreset.CompletionUrl, jsonData, Logger);

        var json = JsonSerializer.Deserialize<ApiResponse>(responseBody)!;
        return json;
    }

    private static int EstimateTokens(List<OpenAiMessage> messages, int from, int count)
    {
        int charCount = 0;
        for (int i = from; i < from + count && i < messages.Count; i++)
            charCount += (messages[i].Content ?? string.Empty).Length;
        return charCount / 2;
    }

    private static int FindSafeCutIndex(List<OpenAiMessage> history, int startIndex)
    {
        int cutIndex = startIndex;
        while (cutIndex < history.Count && history[cutIndex].Role == TOOL)
            cutIndex++;
        return cutIndex;
    }

    private async Task<string> CompressHistoryAsync(List<OpenAiMessage> messagesToCompress)
    {
        var sb = new StringBuilder();
        sb.AppendLine("请将以下对话历史压缩为简洁的摘要，保留关键信息、决定和上下文。");
        sb.AppendLine("摘要应该让读者能理解之前讨论了什么、做了什么决定、执行了哪些操作及其结果。");
        sb.AppendLine("不要添加对话中没有的信息。用中文回复。");
        sb.AppendLine();
        sb.AppendLine("--- 对话历史 ---");
        foreach (var msg in messagesToCompress)
        {
            string roleLabel = msg.Role switch
            {
                USER => "用户",
                ASSISTANT => "助手",
                TOOL => "工具调用结果",
                SYSTEM => "系统",
                _ => msg.Role
            };
            string content = msg.Content ?? string.Empty;
            if (content.Length > 2000)
                content = string.Concat(content.AsSpan(0, 2000), "...[截断]");
            sb.AppendLine($"[{roleLabel}]: {content}");
        }

        HttpClient usedClient;
        string usedModel;
        string completionUrl;
        if (CompressionModel != null && CompressionToken != null)
        {
            if (_compressionClient == null)
            {
                _compressionClient = new HttpClient();
                _compressionClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {CompressionToken}");
            }
            usedClient = _compressionClient;
            usedModel = CompressionModel.model;
            completionUrl = CompressionModel.CompletionUrl;
        }
        else
        {
            usedClient = client;
            usedModel = ModelPreset.model;
            completionUrl = ModelPreset.CompletionUrl;
        }

        var requestMessages = new List<OpenAiMessage>
        {
            new OpenAiMessage { Role = SYSTEM, Content = "你是一个对话历史压缩助手。" },
            new OpenAiMessage { Role = USER, Content = sb.ToString() }
        };
        var requestData = new Dictionary<string, object>
        {
            { "model", usedModel },
            { "messages", requestMessages }
        };
        string jsonData = JsonSerializer.Serialize(requestData, options);
        string responseBody = await SendRequestAsync(usedClient, completionUrl, jsonData, Logger);
        var response = JsonSerializer.Deserialize<ApiResponse>(responseBody)!;
        // 防御：Message 可能为 null；Content 理论上有值，但 provider 异常时可能为 null
        return response.Choices[0].Message?.Content ?? string.Empty;
    }

    public void Dispose()
    {
        client.Dispose();
        _compressionClient?.Dispose();
        GC.SuppressFinalize(this);
    }


    private JsonSerializerOptions options = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };


}

// 创建自定义转换器
public class MessageConverter : JsonConverter<OpenAiMessage>
{
    public override OpenAiMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return JsonSerializer.Deserialize<OpenAiMessage>(root.GetRawText(), options)!;
    }

    public override void Write(Utf8JsonWriter writer, OpenAiMessage value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType());
    }
}
