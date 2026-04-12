using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ZhipuClient;
public enum ImageInterpreterType{
    Normal,Quick
}
public class ImageInterpreter
{
    private readonly ModelPreset modelPreset;
    private readonly OpenAIClient client;
    private readonly ChatClient chatClient;
    const string normalPrompt = "描述图片内容";
    const string quickPrompt = "简要描述图片大致内容";
    public ImageInterpreter(ModelPreset modelPreset, string apiKey)
    {
        this.modelPreset = modelPreset;

        client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(modelPreset.url)
        });
        chatClient = client.GetChatClient(modelPreset.model);
    }

    public async Task<string> Interpret(string imageUrl, ImageInterpreterType type = ImageInterpreterType.Normal)
    {
        return await InterpretInternal(ChatMessageContentPart.CreateImagePart(new Uri(imageUrl)), GetPrompt(type), type);
    }

    public async Task<string> Interpret(byte[] image, ImageInterpreterType type = ImageInterpreterType.Normal)
    {
        return await InterpretInternal(ChatMessageContentPart.CreateImagePart(new BinaryData(image), "image/jpeg"), GetPrompt(type), type);
    }

    string GetPrompt(ImageInterpreterType type)
    {
        return type switch
        {
            ImageInterpreterType.Quick => quickPrompt,
            _ => normalPrompt
        };
    }

    async Task<string> InterpretInternal(ChatMessageContentPart imagePart, string prompt, ImageInterpreterType type)
    {
        var chatOptions = new ChatCompletionOptions
        {
            Temperature = 0
        };
        if (type == ImageInterpreterType.Quick)
        {
            chatOptions.MaxOutputTokenCount = 100;
        }

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
