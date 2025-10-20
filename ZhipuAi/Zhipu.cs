using CommonLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace ZhipuClient;

public class ZhipuAi : IAiClient
{
    string token;
    string apiUrl;
    public string model;
    public bool EnableModelThinking;
    public const string SYSTEM = "system";
    public const string USER = "user";
    public const string ASSISTANT = "assistant";
    public const string TOOL = "tool";
    //finish reason
    public const string STOP= "stop";
    public const string TOOL_CALL= "tool_calls";
    public const string LENGTH= "length";
    public const string SENSITIVE= "sensitive";
    public const string NETWORK_ERROR= "network_error";
    HttpClient client = new HttpClient();
    List<ToolDef> Tools { get; set; } = new();
    Dictionary<string,FunctionDef> functionMapper=new();
    public bool UseDynamicPrompt { get; set; } = true;
    readonly string prompt;
    readonly Browser browser = new();
    public ISimpleLogger Logger { set; private get; } = ConsoleLogger.Instance;
    public ZhipuAi(string token,string prompt, ModelPreset modelPreset)
    {
        this.token = token;
        this.prompt = prompt;
        model = modelPreset.model;
        EnableModelThinking = modelPreset.thinking;
        apiUrl = modelPreset.url;
        SystemPrompt = new ZhipuMessage()
        {
            Role = SYSTEM,
            Content=prompt,
        };
        // 创建HttpClient并设置请求头
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        //set timeout
        client.Timeout = TimeSpan.FromSeconds(50);
        options.Converters.Add(new MessageConverter());
        //tools
        AddBuiltInTools();

    }
    /// <summary>
    /// register built-in tools
    /// </summary>
    private void AddBuiltInTools()
    {
        var watch = new ToolDef();
        watch.Function.Name = "get_time";
        watch.Function.Description = "查看现在的时间";
        watch.Function.FunctionCall = async (parameters) => "北京时间:" + DateTime.Now.ToString();
        RegisterTool(watch);
        var weiboHot= new ToolDef();
        weiboHot.Function.Name = "view_weibo_hot";
        weiboHot.Function.Description = "查看微博热搜";
        weiboHot.Function.FunctionCall = async (parameters) => await browser.GetWeiboHot();
        RegisterTool(weiboHot);
        var browserDef = new ToolDef();
        browserDef.Function.Name = "view_web";
        browserDef.Function.Description = "查看网页主要内容";
        browserDef.Function.Parameters.AddRequired("url", new ParameterProperty() { Type = "string", Description = "需要访问的网址" });
        browserDef.Function.FunctionCall = async (parameters) =>
        {
            var url = parameters["url"];
            var html = await browser.View(url.GetString());
            if (html.Length > 5000)
            {
                html = string.Concat(html.AsSpan(0, 5000), "[省略过长内容]");
            }
            return html;
        };
        RegisterTool(browserDef);
        var bingSearch = new ToolDef();
        bingSearch.Function.Name = "search";
        bingSearch.Function.Description = "使用Bing进行网络搜索";
        bingSearch.Function.Parameters.AddRequired("query", new ParameterProperty() { Type = "string", Description = "keyword" });
        bingSearch.Function.Parameters.AddNonRequired("internationalVersion", new ParameterProperty() { Type = "boolean", Description = "是否启用国际版搜索" });
        bingSearch.Function.FunctionCall = async (parameters) =>
        {
            var query = parameters["query"];
            var internationalVersion = false;
            if(parameters.TryGetValue("internationalVersion",out var v))
            {
                internationalVersion = v.GetBoolean();
            }
            var result = await browser.Search(query.GetString(),internationalVersion);
            return result;
        };
        bingSearch.DynamicPrompt = "网络搜索时，优先使用国内版。";
        RegisterTool(bingSearch);
    }
    /// <summary>
    /// register tool so that it can be called by assistant
    /// </summary>
    /// <param name="tool"></param>
    public void RegisterTool(ToolDef tool)
    {
        Tools.Add(tool);
        functionMapper.Add(tool.Function.Name, tool.Function);
    }
    readonly Dictionary<long, List<ZhipuMessage>> history = new();
    readonly Dictionary<long, SemaphoreSlim> mutex = new();
    readonly Lock mutexMutex =new();
    public ReadOnlySpan<ZhipuMessage> GetDialogHistory(long uid)
    {
        history.TryGetValue(uid,out var dialog);
        if(dialog == null)
        {
            return Span<ZhipuMessage>.Empty;
        }
        return CollectionsMarshal.AsSpan(dialog);
    }
    SemaphoreSlim EnsureMutexExists(long groupId)
    {
        if (!mutex.ContainsKey(groupId))
        {
            lock (mutexMutex)
            {
                if (!mutex.ContainsKey(groupId))
                {
                    mutex.Add(groupId, new SemaphoreSlim(1));
                }
            }
        }   
        return mutex[groupId];
    }
    ZhipuMessage SystemPrompt;
    /// <summary>
    /// reset dialog for a group
    /// </summary>
    /// <param name="id"></param>
    public void Reset(long id)
    {
        var mutex=EnsureMutexExists(id);
        mutex.Wait();
        history.Remove(id);
        mutex.Release(); 
    }
    public TimeSpan AutoNewSpan = TimeSpan.FromHours(12);
    /// <summary>
    /// 处理请求
    /// </summary>
    /// <param name="content">询问内容</param>
    /// <param name="id">区分不同对话的id</param>
    /// <param name="sender">发送者</param>
    /// <param name="specialTag">一个tag，该tag会出现在function call的参数中</param>
    /// <returns>异步字符串迭代器，模型返回结果</returns>
    public async IAsyncEnumerable<string> Ask(string content,long id,string sender,long specialTag=0)
    {
        var mutex=EnsureMutexExists(id);
        if (mutex.CurrentCount == 0)
        {
            yield return "上一个请求尚未完成";
            yield break;
        }
        bool done = false;
        mutex.Wait();
        //if last message is too old, start a new conversation
        if (history.TryGetValue(id, out List<ZhipuMessage>? value))
        {
            var lastMessage = value.LastOrDefault();
            if (lastMessage != null)
            {
                if(DateTime.Now-lastMessage.time> AutoNewSpan)
                {
                    history.Remove(id);
                }
            }
        }

        if (!history.TryGetValue(id, out List<ZhipuMessage>? currentHistory))
        {
            currentHistory = new List<ZhipuMessage>();
            history.Add(id, currentHistory);
            ZhipuMessage prompt;
            
            if (UseDynamicPrompt)
            {
                StringBuilder sb = new(SystemPrompt.Content);
                sb.AppendLine($"\n这段对话的开始时间是{DateTime.Now.ToString("yyyy-MM-dd HH:mm")}");
                var usableTools = await GetUsableToolsByTag(specialTag);
                foreach(var tool in usableTools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.DynamicPrompt))
                    {
                        sb.AppendLine(tool.DynamicPrompt);
                    }
                }
                prompt = new() { 
                    Role=SystemPrompt.Role,
                    Content=sb.ToString(),
                };
            }
            else
            {
                prompt = SystemPrompt;
            }
            history[id].Add(prompt);
        }

        var userQuery = new ZhipuMessage()
        {
            Role = USER,
            Content = $"[用户:{sender}]{content}"
        };
        currentHistory.Add(userQuery);
        while (!done)
        {
            string response;
            try
            {
                var aiResponse = await request(currentHistory,specialTag);
                var msg= aiResponse.Choices[0].Message;
                response = msg.Content;
                if (aiResponse.Choices[0].FinishReason == TOOL_CALL)
                {
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
                    }
                    currentHistory.Add(assistantMessage);
                    //tool call
                    List<Task<ToolMessage>> tasks = new();
                    foreach (var f in aiResponse.Choices[0].Message.ToolCalls)
                    {
                        tasks.Add(HandleFunctionCall(f.Function,f.Id,specialTag));
                    }
                    await Task.WhenAll(tasks);
                    foreach(var i in tasks)
                    {
                        currentHistory.Add(i.Result);
                    }
                }
                else
                {

                    currentHistory.Add(new ZhipuMessage()
                    {
                        Role = msg.Role,
                        Content = msg.Content
                    });
                    done =true;
                }
            }
            catch (Exception e)
            {
                response = "Error: " + e.Message;
                done = true;
            }

            if (!string.IsNullOrEmpty(response))
            {
                yield return response.Trim();
            }
        }
        mutex.Release();


    }
    async Task<ToolMessage> HandleFunctionCall(Function func,string id,long specialTag)
    {
        ToolMessage message = new();
        message.Role = TOOL;
        message.Id = id;
        functionMapper.TryGetValue(func.Name,out var tool);
        Logger.Info($"FuncCall:{func.Name} {func.Arguments}");
        if (tool != null)
        {
            try
            {
                var args = JsonSerializer.Deserialize<FunctionCallArguments>(func.Arguments);
                args.SpecialTag = specialTag;
                message.Content = await tool.FunctionCall.Invoke(args);
                Logger.Info("function result:"+message.Content);
            }
            catch(Exception e)
            {
                message.Content = "调用失败: " + e.Message;
                Logger.Warn("function error:"+e.Message);
            }
        }
        else
        {
            message.Content = "Error: " + func.Name + " not found";
            Logger.Warn("function not found:"+func.Name);
        }
        
        return message;
    }
    Dictionary<long, List<ToolDef>> _usableToolsCache = new();
    internal async Task<List<ToolDef>> GetUsableToolsByTag(long tag)
    {
        if(_usableToolsCache.TryGetValue(tag, out var cache))
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
    public async Task<ApiResponse> request(IEnumerable<ZhipuMessage> messages, long specialTag)
    {
        var usableFunctionCall= await GetUsableToolsByTag(specialTag);
        // 创建请求数据
        var requestData = new
        {
            model = model,
            messages = messages,
            tools = usableFunctionCall,
            thinking = new
            {
                type= EnableModelThinking?"enabled":"disabled",
            },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, apiUrl);

        // 序列化请求数据为JSON
        string jsonData = JsonSerializer.Serialize(requestData,options);
        req.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

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
                    foreach(var i in err.ContentFilters)
                    {
                        sb.Append($"[{i.Role}:{i.Level}]");
                    }
                    throw new Exception(sb.ToString());
                }catch(Exception e){}
                throw new Exception(rep);
            }
            catch(Exception e){ }
            throw new HttpRequestException($"API请求失败: {response.StatusCode}");
        }
        // 读取并输出响应内容
        string responseBody = await response.Content.ReadAsStringAsync();
        //Console.WriteLine("API响应:");
        //Console.WriteLine(responseBody);

        var json = JsonSerializer.Deserialize<ApiResponse>(responseBody)!;
        return json;
    }


    // 使用方式
    JsonSerializerOptions options = new JsonSerializerOptions();
    
    
}

// 创建自定义转换器
public class MessageConverter : JsonConverter<ZhipuMessage>
{
    public override ZhipuMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return JsonSerializer.Deserialize<ZhipuMessage>(root.GetRawText(), options);
    }

    public override void Write(Utf8JsonWriter writer, ZhipuMessage value, JsonSerializerOptions options)
    {
            JsonSerializer.Serialize(writer, value, value.GetType());
    }
}
