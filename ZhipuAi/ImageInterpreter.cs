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
        return await InterpretInternal(ChatMessageContentPart.CreateImagePart(new Uri(imageUrl)));
    }

    public async Task<string> Interpret(byte[] image)
    {
        return await InterpretInternal(ChatMessageContentPart.CreateImagePart(new BinaryData(image), "image/jpeg"));
    }

    async Task<string> InterpretInternal(ChatMessageContentPart imagePart)
    {
        var chatOptions = new ChatCompletionOptions
        {
            Temperature = 0
        };

        var messages = new List<ChatMessage>
        {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(prompt),
                imagePart)
        };

        var response = await chatClient.CompleteChatAsync(messages, chatOptions);
        return response.Value.Content[0].Text;
    }
}
