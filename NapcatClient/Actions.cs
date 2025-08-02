using WebSocketSharp;
using CommonLib;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace NapcatClient.Action;

public class Actions
{
    readonly WebSocket WebSocket;
    readonly ISimpleLogger Logger;
    BotClient bot;
    public Actions(WebSocket WebSocket, ISimpleLogger logger, BotClient bot)
    {
        this.WebSocket = WebSocket;
        Logger = logger;
        this.bot = bot;
    }
    private static SemaphoreSlim responseSemaphore = new SemaphoreSlim(0);
    private ulong echoCount = 0;
    public Task<Dictionary<string, JsonElement>> _SendAction(ParameteredAct act)
    {
        return _SendAction(act.ToAct());
    }
    public async Task<Dictionary<string, JsonElement>> _SendAction(Act act)
    {
        var echo = $"{echoCount++}";
        act.Echo = echo;
        var json = BotUtils.Serilize(act);
        Logger.Info($"sending: {json}");
        await Task.Run(() =>
        {
            WebSocket.Send(json);
        });
        return await WaitForResponse(echo);
    }
    internal void AddResponse(string echo, Dictionary<string, JsonElement> response)
    {
        Logger.Info($"return: {echo}");
        responses.Add(echo, response);
        responseSemaphore.Release();
    }
    Dictionary<string, Dictionary<string, JsonElement>> responses = new();
    public async Task<Dictionary<string, JsonElement>> WaitForResponse(string echo)
    {
        await responseSemaphore.WaitAsync();
        if (responses.ContainsKey(echo))
        {
            var res = responses[echo];
            responses.Remove(echo);
            return res;
        }
        else
        {
            responseSemaphore.Release();
            await Task.Yield();
            return await WaitForResponse(echo);
        }
    }
    public async Task<Dictionary<string, JsonElement>> SendGroupMessage(long groupId, IEnumerable<Message> messageChain)
    {
        Dictionary<string, dynamic> parameters = new();
        parameters["group_id"] = groupId;
        parameters["message"] = messageChain;
        ParameteredAct act = new ParameteredAct(
            "send_group_msg",
            parameters
            );
        return await _SendAction(act);
    }
    public async Task<Dictionary<string, JsonElement>> SendGroupMessage(long groupId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }
    public async Task<Dictionary<string, JsonElement>> ReplyGroupMessage(long groupId,long messageId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Reply(messageId));
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }
    const int PART_LENGTH = 500;
    public Task<Dictionary<string, JsonElement>> SendLongMessage(string groupId, string text,string nickname)
    {
        var fowardChain = GroupForwardChain.BuildDefault(bot.SelfId.ToString(),nickname,groupId);
        var text_char = text.ToCharArray();

        for (int i = 0; i <= text_char.Length / PART_LENGTH; i++)
        {
            int start = i * PART_LENGTH;
            int end = (i + 1) * PART_LENGTH;

            if (end < text_char.Length)
            {
                fowardChain.AddText(new string(text_char,start, PART_LENGTH));
            }
            else
            {
                fowardChain.AddText(new string(text_char, start,text_char.Length-start));
            }
        }
        Act act = new("send_group_forward_msg",fowardChain);
        return _SendAction(act);

    }
    public Task<Dictionary<string, JsonElement>> SendGroupAiVoice(string groupId, string text,string character= "lucy-voice-suxinjiejie")
    {
        ParameteredAct act = new(
            action: "send_group_ai_record",
            parameters: new Dictionary<string, dynamic>()
            {
                ["group_id"] = groupId,
                ["character"] = character,
                ["text"] = text
            }
        );
        return _SendAction(act);
    }
    public async Task<(long userId,string nickname)> GetAccountInfo()
    {
        Act act = new(
            action: "get_login_info",
            parameters: new object()
        );
        var result=await _SendAction(act);
        var data = result["data"];
        long userId=data.GetProperty("user_id").GetInt64();
        string nickname=data.GetProperty("nickname").GetString();
        return (userId,nickname);
    }


}
public class Act
{
    public Act() { }
    public Act(string action, dynamic parameters)
    {
        this.Action = action;
        this.Parameters = parameters;
    }
    [JsonPropertyName("action")]
    public string Action { set; get; }
    [JsonPropertyName("params")]
    public dynamic Parameters { set; get; } = new object();

    [JsonPropertyName("echo")]
    public string Echo { internal set; get; } = string.Empty;
}
public class ParameteredAct
{
    public ParameteredAct(string action, Dictionary<string, dynamic> parameters)
    {
        this.Action = action;
        this.Parameters = parameters;
    }
    
    [JsonPropertyName("action")]
    public string Action { set; get; }
    [JsonPropertyName("params")]
    public Dictionary<string, dynamic> Parameters { set; get; }

    [JsonPropertyName("echo")]
    public string Echo { internal set; get; } = string.Empty;
    public Act ToAct()
    {
        var tmp= new Act(this.Action, this.Parameters);
        tmp.Echo = this.Echo;
        return tmp;
    }

}
