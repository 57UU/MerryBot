using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhipuClient;

public class ImagePainterDashscope
{
    private readonly DashscopeModelPreset _modelPreset;
    private readonly HttpClient _httpClient;
    private readonly string _apiToken;

    public ImagePainterDashscope(DashscopeModelPreset dashscopeModelPreset, string apiToken)
    {
        _modelPreset = dashscopeModelPreset;
        _httpClient = new HttpClient();
        _apiToken = apiToken;
    }
    /// <summary>
    /// 绘制图片
    /// </summary>
    /// <param name="prompt">图片描述</param>
    /// <param name="negativePrompt">负提示词</param>
    /// <param name="width">图片宽度</param>
    /// <param name="height">图片高度</param>
    /// <returns>图片URL</returns>
    public async Task<string> DrawImage(string prompt, string? negativePrompt = null, int width = 1024, int height = 1024)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _modelPreset.ImageGenerateUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        request.Headers.Add("X-DashScope-Async", "disable");

        var requestBody = new ImageGenerateRequest
        {
            Model = _modelPreset.model,
            Input = new ImageGenerateInput
            {
                Messages = new List<MessageItem>
                {
                    new MessageItem
                    {
                        Role = "user",
                        Content = new List<ContentItem>
                        {
                            new ContentItem { Text = prompt }
                        }
                    }
                }
            },
            Parameters = new ImageGenerateParameters
            {
                Size = $"{width}*{height}",
                NegativePrompt = negativePrompt ?? string.Empty,
                PromptExtend = true,
                Watermark = false
            }
        };

        string jsonData = JsonSerializer.Serialize(requestBody);
        request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            string errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Image generation failed with status {response.StatusCode}: {errorContent}");
        }

        string responseContent = await response.Content.ReadAsStringAsync();
        var responseObj = JsonSerializer.Deserialize<ImageGenerateResponse>(responseContent);

        if (responseObj?.Output?.Choices?.Count > 0)
        {
            var firstChoice = responseObj.Output.Choices[0];
            var imageUrl = firstChoice.Message?.Content?.FirstOrDefault()?.Image;
            return imageUrl
                ?? throw new InvalidOperationException("Image URL is null");
        }

        if (!string.IsNullOrEmpty(responseObj?.Output?.TaskId))
        {
            throw new InvalidOperationException($"Image generation is async. Task ID: {responseObj.Output.TaskId}. Please use async polling method.");
        }

        throw new InvalidOperationException($"Unexpected response format: {responseContent}");
    }
}

public class ImageGenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public ImageGenerateInput Input { get; set; } = new();

    [JsonPropertyName("parameters")]
    public ImageGenerateParameters Parameters { get; set; } = new();
}

public class ImageGenerateInput
{
    [JsonPropertyName("messages")]
    public List<MessageItem> Messages { get; set; } = new();
}

public class MessageItem
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<ContentItem> Content { get; set; } = new();
}

public class ContentItem
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class ImageGenerateParameters
{
    [JsonPropertyName("size")]
    public string Size { get; set; } = "1024*1024";

    [JsonPropertyName("negative_prompt")]
    public string NegativePrompt { get; set; } = string.Empty;

    [JsonPropertyName("prompt_extend")]
    public bool PromptExtend { get; set; } = true;

    [JsonPropertyName("watermark")]
    public bool Watermark { get; set; } = false;
}

public class ImageGenerateResponse
{
    [JsonPropertyName("output")]
    public ImageGenerateOutput? Output { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }
}

public class ImageGenerateOutput
{
    [JsonPropertyName("choices")]
    public List<ImageGenerateChoice>? Choices { get; set; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }
}

public class ImageGenerateChoice
{
    [JsonPropertyName("message")]
    public MessageContent? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class MessageContent
{
    [JsonPropertyName("content")]
    public List<ImageContent>? Content { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

public class ImageContent
{
    [JsonPropertyName("image")]
    public string? Image { get; set; }
}
