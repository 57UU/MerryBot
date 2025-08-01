using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using ZhipiAi;
using CommonLib;

namespace ZhipuClient;

public class ZhipuAi
{
    string token;
    string apiUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    string model = "glm-4.5";
    const string SYSTEM = "system";
    const string USER = "user";
    const string ASSISTANT = "assistant";
    const string TOOL = "tool";
    //finish reason
    const string STOP= "stop";
    const string TOOL_CALL= "tool_calls";
    const string LENGTH= "length";
    const string SENSITIVE= "sensitive";
    const string NETWORK_ERROR= "network_error";
    HttpClient client = new HttpClient();
    List<ToolDef> Tools { get; set; } = new();
    Dictionary<string,FunctionDef> funtionMapper=new();
    string prompt;
    Browser browser = new();
    public ISimpleLogger Logger { set; private get; } = new ConsoleLogger();
    public ZhipuAi(string token,string prompt)
    {
        this.token = token;
        this.prompt = prompt;
        SystemPrompt = new ZhipuMessage()
        {
            Role = SYSTEM,
            Content=prompt,
        };
        // 创建HttpClient并设置请求头
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        options.Converters.Add(new MessageConverter());
        //tools
        var watch = new ToolDef();
        watch.Function.Name = "getTime";
        watch.Function.Description = "查看现在的时间";
        watch.Function.FunctionCall = async(parameters) => "北京时间:" + DateTime.Now.ToString();
        RegisterTool(watch);
        var browserDef = new ToolDef();
        browserDef.Function.Name = "view_web";
        browserDef.Function.Description = "查看网页主要HTML内容";
        browserDef.Function.Parameters.Properties.Add("url", new ParameterProperty() { Type = "string", Description = "需要访问的网址" });
        browserDef.Function.FunctionCall = async(parameters) =>
        {
            var p = parameters.GetString();
            var para=JsonSerializer.Deserialize<Dictionary<string,string>>(p);
            var url=para["url"];
            var html= await browser.view(url);
            if (html.Length > 4000)
            {
                html = html.Substring(0, 3500)+"[省略过长内容]";
            }
            return $"该网页主要内容如下:{html}";
        };
        RegisterTool(browserDef);
        var bingSearch = new ToolDef();
        bingSearch.Function.Name = "bing_search";
        bingSearch.Function.Description = "使用Bing进行网络搜索";
        bingSearch.Function.Parameters.Properties.Add("query", new ParameterProperty() { Type = "string", Description = "keyword" });
        bingSearch.Function.FunctionCall = async(parameters) =>
        {
            var p = parameters.GetString();
            var para = JsonSerializer.Deserialize<Dictionary<string, string>>(p);
            var query = para["query"];
            var result = await browser.Search(query);
            return $"搜索结果如下:{result}";
        };
        RegisterTool(bingSearch);

    }
    void RegisterTool(ToolDef tool)
    {
        Tools.Add(tool);
        funtionMapper.Add(tool.Function.Name, tool.Function);
    }
    Dictionary<long, List<ZhipuMessage>> history = new();
    Dictionary<long, SemaphoreSlim> mutex = new();
    object mutexMutex=new();
    SemaphoreSlim ensureMutexExists(long groupId)
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
    
    public void Reset(long id)
    {
        var mutex=ensureMutexExists(id);
        mutex.Wait();
        if (history.ContainsKey(id))
        {
            history[id].Clear();
            history[id].Add(SystemPrompt);
        }
        mutex.Release();
        

    }
    public async IAsyncEnumerable<string> Ask(string content,long id,string sender)
    {
        var mutex=ensureMutexExists(id);
        if (mutex.CurrentCount == 0)
        {
            yield return "上一个请求尚未完成";
            yield break;
        }
        bool done = false;
        mutex.Wait();
        
        if (!history.ContainsKey(id))
        {
            history.Add(id, new List<ZhipuMessage>());
            history[id].Add(SystemPrompt.GenerateWithTime());
        }
        var currentHistory = history[id];
        var userQuery = new ZhipuMessage()
        {
            Role = USER,
            Content = $"[发送者:{sender}]{content}"
        };
        currentHistory.Add(userQuery);
        while (!done)
        {
            string response;
            try
            {
                var aiResponse = await request(currentHistory);
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
                    foreach (var f in aiResponse.Choices[0].Message.ToolCalls)
                    {
                        currentHistory.Add(await HandleFunctionCall(f.Function,f.Id));
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

            response = response.Trim();
            if (!string.IsNullOrEmpty(response))
            {
                yield return response;
            }
        }
        mutex.Release();


    }
    async Task<ToolMessage> HandleFunctionCall(Function func,string id)
    {
        ToolMessage message = new();
        message.Role = TOOL;
        message.Id = id;
        funtionMapper.TryGetValue(func.Name,out var tool);
        Logger.Info($"FuncCall:{func.Name} {func.Arguments}");
        if (tool != null)
        {
            try
            {
                message.Content = await tool.FunctionCall.Invoke(func.Arguments);
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
    public async Task<ApiResponse> request(IEnumerable<ZhipuMessage> messages)
    {
        // 创建请求数据
        var requestData = new
        {
            model = model,
            messages = messages,
            tools = Tools,
            thinking = new
            {
                type= "enabled",
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
        response.EnsureSuccessStatusCode();
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

public class ZhipuMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }
    public ZhipuMessage GenerateWithTime()
    {
        return new ZhipuMessage()
        {
            Role = Role,
            Content = Content + $"\n现在是{DateTime.Now}",
        };
    }
}
public class AssistantMessage : ZhipuMessage
{
    [JsonPropertyName("tool_calls")]
    public List<ToolCallSubMessage> ToolCalls { get; set; } = new ();
}
public class ToolMessage : ZhipuMessage
{ 
    [JsonPropertyName("tool_call_id")]
    public string Id { get; set; }
}
public class ToolCallSubMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    [JsonPropertyName("function")]
    public Function Function { get; set; }
}




// 单个工具
public class ToolDef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function"; // 固定为function

    [JsonPropertyName("function")]
    public FunctionDef Function { get; set; } = new FunctionDef();
}


public delegate Task<string> FunctionCall(JsonElement parameter);
// 函数信息
public class FunctionDef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } // 函数名称（如getWeather）

    [JsonPropertyName("description")]
    public string Description { get; set; } // 函数描述

    [JsonPropertyName("parameters")]
    public ParameterSchema Parameters { get; set; } = new ParameterSchema();
    [JsonIgnore]
    public FunctionCall FunctionCall { get; set; }
}

// 参数 schema 定义
public class ParameterSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object"; // 固定为object

    [JsonPropertyName("properties")]
    public Dictionary<string, ParameterProperty> Properties { get; set; } = new Dictionary<string, ParameterProperty>();

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = new List<string>(); // 必选参数列表
}

// 具体参数属性
public class ParameterProperty
{
    [JsonPropertyName("type")]
    public string Type { get; set; } // 参数类型（如string）

    [JsonPropertyName("description")]
    public string Description { get; set; } // 参数描述
}


public class ApiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; }

    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = new List<Choice>();

    [JsonPropertyName("usage")]
    public Usage Usage { get; set; }

    [JsonPropertyName("video_result")]
    public List<VideoResult> VideoResults { get; set; } = new List<VideoResult>();

    [JsonPropertyName("web_search")]
    public List<WebSearchResult> WebSearchResults { get; set; } = new List<WebSearchResult>();

    [JsonPropertyName("content_filter")]
    public List<ContentFilter> ContentFilters { get; set; } = new List<ContentFilter>();
}

public class Choice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public Message Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; }
}

public class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string ReasoningContent { get; set; }

    [JsonPropertyName("audio")]
    public Audio Audio { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();
}

public class Audio
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("data")]
    public string Data { get; set; }

    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; }
}

public class ToolCall
{
    [JsonPropertyName("function")]
    public Function Function { get; set; }

    [JsonPropertyName("mcp")]
    public Mcp Mcp { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }
}

public class Function
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; } = new();
}

public class Mcp
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("server_label")]
    public string ServerLabel { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; }

    [JsonPropertyName("tools")]
    public List<Tool> Tools { get; set; } = new List<Tool>();

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("output")]
    public Dictionary<string, object> Output { get; set; } = new Dictionary<string, object>();
}

public class Tool
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("annotations")]
    public Dictionary<string, object> Annotations { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("input_schema")]
    public InputSchema InputSchema { get; set; }
}

public class InputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = new List<string>();

    [JsonPropertyName("additionalProperties")]
    public bool AdditionalProperties { get; set; }
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class VideoResult
{
    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("cover_image_url")]
    public string CoverImageUrl { get; set; }
}

public class WebSearchResult
{
    [JsonPropertyName("icon")]
    public string Icon { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("link")]
    public string Link { get; set; }

    [JsonPropertyName("media")]
    public string Media { get; set; }

    [JsonPropertyName("publish_date")]
    public string PublishDate { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }

    [JsonPropertyName("refer")]
    public string Refer { get; set; }
}

public class ContentFilter
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }
}
