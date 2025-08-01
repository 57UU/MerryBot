using Newtonsoft.Json;
using WebSocketSharp;

namespace NapcatClient.Action;

public class Actions
{
    readonly WebSocket WebSocket;
    readonly ISimpleLogger Logger;
    public Actions(WebSocket WebSocket, ISimpleLogger logger)
    {
        this.WebSocket = WebSocket;
        Logger = logger;
    }
    private static SemaphoreSlim responseSemaphore = new SemaphoreSlim(0);
    private ulong echoCount = 0;
    public async Task<Dictionary<string, dynamic>> _SendAction(Act act)
    {
        var echo = $"{echoCount++}";
        act.Echo = echo;
        var json = BotUtils.serilize(act);
        Logger.Info($"sending: {json}");
        await Task.Run(() =>
        {
            WebSocket.Send(json);
        });
        return await WaitForResponse(echo);
    }
    internal void AddResponse(string echo, Dictionary<string, dynamic> response)
    {
        Logger.Info($"return: {echo}");
        responses.Add(echo, response);
        responseSemaphore.Release();
    }
    Dictionary<string, Dictionary<string, dynamic>> responses = new();
    public async Task<Dictionary<string, dynamic>> WaitForResponse(string echo)
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
    public async Task<Dictionary<string, dynamic>> SendGroupMessage(long groupId, IEnumerable<Message> messageChain)
    {
        Dictionary<string, dynamic> parameters = new();
        parameters["group_id"] = groupId;
        parameters["message"] = messageChain;
        Act act = new Act(
            "send_group_msg",
            parameters
            );
        return await _SendAction(act);
    }
    public async Task<Dictionary<string, dynamic>> SendGroupMessage(long groupId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }
    public async Task<Dictionary<string, dynamic>> ReplyGroupMessage(long groupId,long messageId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Reply(messageId));
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }


}
public class Act
{
    public Act(string action, Dictionary<string, dynamic> parameters)
    {
        this.Action = action;
        this.Parameters = parameters;
    }
    [JsonProperty(PropertyName = "action")]
    public string Action { set; get; } = string.Empty;
    [JsonProperty(PropertyName = "params")]
    public Dictionary<string, dynamic> Parameters { set; get; }
    [JsonProperty(PropertyName = "echo")]
    public string Echo { internal set; get; } = string.Empty;
}