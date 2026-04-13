using BrowserService;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ZhipuClient;

public partial class ZhipuAi : IDisposable
{
    string token;
#pragma warning disable CS8625 // 无法将 null 字面量转换为非 null 的引用类型。
    public ModelPreset ModelPreset { get; private set; } = null;
#pragma warning restore CS8625 // 无法将 null 字面量转换为非 null 的引用类型。

    HttpClient client = new HttpClient();
    private List<ToolDef> Tools { get; set; } = new();
    private Dictionary<string, ToolDef> functionMapper = new();

    readonly string prompt;
    public static readonly Browser browser = new(new BrowserOptions { BinaryPath = Environment.GetEnvironmentVariable("CHROME_BIN") });
    public ZhipuAi(string token, string prompt, ModelPreset modelPreset, HistoryRecorder? historyRecorder = null, bool useBuildinTools = true)
    {
        this.token = token;
        this.prompt = prompt;
        this.HistoryRecorder = historyRecorder;
        SystemPrompt = new ZhipuMessage()
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
        RegisterBrowser();

        //RegisterWeiboHot();

        if (ModelPreset.enableSearch)
        {
            RegisterBingSearch();
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
    readonly ConcurrentDictionary<long, List<ZhipuMessage>> history = new();
    readonly ConcurrentDictionary<long, SemaphoreSlim> mutex = new();
    public ReadOnlySpan<ZhipuMessage> GetDialogHistory(long uid)
    {
        history.TryGetValue(uid, out var dialog);
        if (dialog == null)
        {
            return Span<ZhipuMessage>.Empty;
        }
        return CollectionsMarshal.AsSpan(dialog);
    }
    public void SetDialogHistory(long uid, IEnumerable<ZhipuMessage> messages)
    {
        history[uid] = messages.ToList();
    }
    public void AppendDialogHistory(long uid, IEnumerable<ZhipuMessage> messages)
    {
        var currentHistory = history.GetOrAdd(uid, _ => new List<ZhipuMessage>());
        currentHistory.AddRange(messages);
    }
    public string SystemPromptContent => SystemPrompt.Content!;
    SemaphoreSlim EnsureMutexExists(long groupId)
    {
        return mutex.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
    }
    private ZhipuMessage SystemPrompt;
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

    public void AddHistory(long id, ZhipuMessage message)
    {
        var currentHistory = history.GetOrAdd(id, _ => new List<ZhipuMessage>());
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
        var recorder = (ZhipuMessage message) => HistoryRecorder?.Invoke(id, message.Role, message.Content);
        var mutex = EnsureMutexExists(id);
        if (!mutex.Wait(0))
        {
            throw new NotAvailableException("上一个请求尚未完成");
        }
        try
        {
            bool done = false;
        //if last message is too old, start a new conversation
        if (history.TryGetValue(id, out List<ZhipuMessage>? value))
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

        if (!history.TryGetValue(id, out List<ZhipuMessage>? currentHistory))
        {
            //if currentHistory is null, create a new one
            currentHistory = new List<ZhipuMessage>();
            history.TryAdd(id, currentHistory);
        }

        if (currentHistory.Count == 0 || currentHistory[0].Role != SYSTEM)
        {
            ZhipuMessage prompt;
            if (UseDynamicPrompt)
            {
                StringBuilder sb = new(SystemPrompt.Content);
                sb.AppendLine($"\n这段对话的开始时间是{DateTime.Now.ToString("yyyy-MM-dd HH:mm")}");
                var usableTools = await GetUsableToolsByTag(specialTag);
                foreach (var tool in usableTools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.DynamicPrompt))
                    {
                        sb.AppendLine(tool.DynamicPrompt);
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

        var userQuery = new ZhipuMessage()
        {
            Role = USER,
            Content = $"{sender}{content}"
        };
        currentHistory.Add(userQuery);
        recorder(userQuery);
        //if currentHistory is too long, remove the first message
        int excessCount = currentHistory.Count - SlidingWindowContext;
        if (excessCount > 0)
        {
            // 保留第一个元素（系统提示），移除从索引1开始的excessCount个元素
            currentHistory.RemoveRange(1, excessCount);
        }
        while (!done)
        {
            string response;
            bool hideOutput = false;
            try
            {
                var aiResponse = await Request(currentHistory, specialTag);
                var msg = aiResponse.Choices[0].Message;
                if (msg.Content.StartsWith("<think>"))
                {
                    var cotEndIndex = msg.Content.IndexOf("</think>");
                    if (cotEndIndex >= 0)
                    {
                        msg.Content = msg.Content.Substring(cotEndIndex + 8).TrimStart('\n', ' ');
                    }
                }
                response = msg.Content;
                if (aiResponse.Choices[0].FinishReason == TOOL_CALL)
                {
                    ExitAfterUseException? exitMessage = null;
                    var assistantMessage = new AssistantMessage()
                    {
                        Role = msg.Role,
                        Content = msg.Content
                    };
                    foreach (var i in msg.ToolCalls)
                    {
                        assistantMessage.ToolCalls.Add(new ToolCallSubMessage()
                        {
                            Id = i.Id,
                            Function = i.Function
                        });
                        if (functionMapper.TryGetValue(i.Function.Name, out var toolDef))
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
                    var toolCallsStr = string.Join(",", assistantMessage.ToolCalls.Select(i => $"{i.Function.Name}({i.Function.Arguments})"));
                    HistoryRecorder?.Invoke(id, assistantMessage.Role,
                        string.IsNullOrWhiteSpace(assistantMessage.Content) ? toolCallsStr : $"{assistantMessage.Content}:{toolCallsStr}");
                    //tool call
                    List<Task<ToolMessage>> tasks = new();
                    foreach (var f in aiResponse.Choices[0].Message.ToolCalls)
                    {
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
                    var currentMessage = new ZhipuMessage()
                    {
                        Role = msg.Role,
                        Content = msg.Content
                    };
                    currentHistory.Add(currentMessage);
                    recorder(currentMessage);
                    done = true;
                }
            }
            catch (ExitAfterUseException)
            {
                throw;
            }
            catch (Exception e)
            {
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
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");
    public async Task<ApiResponse> Request(IEnumerable<ZhipuMessage> messages, long specialTag)
    {
        var usableFunctionCall = await GetUsableToolsByTag(specialTag);
        // 创建请求数据
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

        var req = new HttpRequestMessage(HttpMethod.Post, ModelPreset.CompletionUrl);

        // 序列化请求数据为JSON
        string jsonData = JsonSerializer.Serialize(requestData, options);
        req.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        req.Content.Headers.ContentType = JsonMediaType;

        // 发送POST请求
        HttpResponseMessage response = await client.SendAsync(req);
        // 确保请求成功
        if (response.StatusCode != HttpStatusCode.OK)
        {
            Logger.Error($"ZhipuAi API Error");
            try
            {
                string rep = await response.Content.ReadAsStringAsync();
                try
                {
                    var err = JsonSerializer.Deserialize<ApiResponse>(rep)!;
                    StringBuilder sb = new("内容问题：");
                    foreach (var i in err.ContentFilters)
                    {
                        sb.Append($"[{i.Role}:{i.Level}]");
                    }
                    throw new Exception(sb.ToString());
                }
                catch (Exception) { }
                throw new Exception(rep);
            }
            catch (Exception) { }
            throw new HttpRequestException($"API请求失败: {response.StatusCode}");
        }
        // 读取并输出响应内容
        string responseBody = await response.Content.ReadAsStringAsync();
        //Console.WriteLine("API响应:");
        //Console.WriteLine(responseBody);

        var json = JsonSerializer.Deserialize<ApiResponse>(responseBody)!;
        return json;
    }

    public void Dispose()
    {
        browser.Dispose();
        client.Dispose();
        GC.SuppressFinalize(this);
    }


    private JsonSerializerOptions options = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };


}

// 创建自定义转换器
public class MessageConverter : JsonConverter<ZhipuMessage>
{
    public override ZhipuMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return JsonSerializer.Deserialize<ZhipuMessage>(root.GetRawText(), options)!;
    }

    public override void Write(Utf8JsonWriter writer, ZhipuMessage value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType());
    }
}
