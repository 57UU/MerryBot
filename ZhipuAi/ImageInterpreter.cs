using System;
using System.Collections.Generic;
using System.Text;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ZhipuClient;

public class ImageInterpreter
{
    private readonly ModelPreset modelPreset;
    private readonly OpenAIClient client;
    private readonly ChatClient chatClient;
    const string prompt = "描述图片内容";
    public ImageInterpreter(ModelPreset modelPreset, string apiKey)
    {
        this.modelPreset = modelPreset;

        client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
             Endpoint = new Uri(modelPreset.url)
        });
        chatClient = client.GetChatClient(modelPreset.model);
    }
        
    public async Task<string> Interpret(string imageUrl)
    {
        var chatOptions = new ChatCompletionOptions
        {
            Temperature=0
        };

        var messages = new List<ChatMessage>
        {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(prompt),
                ChatMessageContentPart.CreateImagePart(new Uri(imageUrl)))
        };

        var response = await chatClient.CompleteChatAsync(messages, chatOptions);
        return response.Value.Content[0].Text;
    }
}
