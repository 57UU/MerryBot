using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhipuClient;

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
public class ZhipuMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }
    [JsonIgnore]
    public DateTime time = DateTime.Now;
}
public class AssistantMessage : ZhipuMessage
{
    [JsonPropertyName("tool_calls")]
    public List<ToolCallSubMessage> ToolCalls { get; set; } = new();
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


public delegate Task<bool> IsUseable(long specialTag);

// 单个工具
public class ToolDef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function"; // 固定为function

    [JsonPropertyName("function")]
    public FunctionDef Function { get; set; } = new FunctionDef();
    [JsonIgnore]
    public IsUseable isUseable = async (tag) => true;
    [JsonIgnore]
    public string? DynamicPrompt { get; set; }

}

public class FunctionCallArguments : Dictionary<string, JsonElement>
{
    public FunctionCallArguments()
    {

    }
    [JsonIgnore]
    public long SpecialTag { get; set; } = 0;
}
public delegate Task<string> FunctionCall(FunctionCallArguments parameter);
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
    public void AddRequired(string name, ParameterProperty parameterProperty)
    {
        Properties.Add(name, parameterProperty);
        Required.Add(name);
    }
    public void AddNonRequired(string name, ParameterProperty parameterProperty)
    {
        Properties.Add(name, parameterProperty);
    }
    public void MarkAllAsRequired()
    {
        Required.AddRange(Properties.Keys);
    }
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
    public string Arguments { get; set; } = string.Empty;
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
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。