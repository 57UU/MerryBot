using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace OpenAiClient;

/// <summary>
/// 轻量的网页内容总结器
/// </summary>
public sealed class WebviewSummarizer : IDisposable
{
    private readonly ChatClient _chatClient;
    private bool _disposed;

    public string Logger { get; set; } = string.Empty;

    public WebviewSummarizer(string apiKey, ModelPreset modelPreset)
    {
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(modelPreset.url)
        });
        _chatClient = client.GetChatClient(modelPreset.model);
    }

    public async Task<string> SummarizeAsync(string content, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("你是一个网页内容总结助手。请简洁地总结以下网页内容，提取关键信息，保持要点清晰。忽略标题栏和导航栏等无关内容。"),
            new UserChatMessage($"总结以下网页内容:\n\n{content}")
        };

        try
        {
            var response = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
            {
                MaxOutputTokenCount = 2000
            }, cancellationToken);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebviewSummarizer error: {ex}");
            return $"[总结请求失败] {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}