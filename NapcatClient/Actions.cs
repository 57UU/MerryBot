using CommonLib;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WebSocketSharp;

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
    public Task<ResponseRootObject> _SendAction(ParameteredAct act)
    {
        return _SendAction(act.ToAct());
    }
    public async Task<ResponseRootObject> _SendAction(Act act)
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
    internal void AddResponse(string echo, ResponseRootObject response)
    {
        Logger.Info($"return: {echo}");
        responses.Add(echo, response);
        responseSemaphore.Release();
    }
    Dictionary<string, ResponseRootObject> responses = new();
    public async Task<ResponseRootObject> WaitForResponse(string echo)
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
    /// <summary>
    /// 在QQ群中发送消息
    /// </summary>
    /// <param name="groupId">qq群号</param>
    /// <param name="messageChain">消息链</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> SendGroupMessage(long groupId, IEnumerable<Message> messageChain)
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
    /// <summary>
    /// 在QQ群中发送文本消息
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> SendGroupMessage(long groupId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }
    /// <summary>
    /// 在QQ群中回复一条消息
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="messageId">要回复的消息的ID</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> ReplyGroupMessage(long groupId,long messageId, string text)
    {
        List<Message> messages = new List<Message>();
        messages.Add(Message.Reply(messageId));
        messages.Add(Message.Text(text));
        return await SendGroupMessage(groupId, messages);
    }
    public int PartLength { set; get; } = 500;
    public string DefaultNickname { get; set; } = "曼瑞";
    /// <summary>
    /// 在QQ群中选择最合适的回复方式（长：转发消息；短：直接回复）
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="messageId">要回复的消息的ID</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public Task<ResponseRootObject> ChooseBestReplyMethod(long groupId, long messageId, string text)
    {
        return ChooseBestReplyMethod(groupId, messageId, text, DefaultNickname);
    }
    /// <summary>
    /// 在QQ群中选择最合适的回复方式（长：转发消息；短：直接回复）
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="messageId">要回复的消息的ID</param>
    /// <param name="text">文本</param>
    /// <param name="nickname">昵称</param>
    /// <returns></returns>
    public Task<ResponseRootObject> ChooseBestReplyMethod(long groupId, long messageId, string text, string nickname)
    {
        if (text.Length > PartLength)
        {
            return SendLongMessage(groupId.ToString(), text, nickname);
        }
        else
        {
            return ReplyGroupMessage(groupId,messageId, text);
        }
    }
    /// <summary>
    /// 发送长消息，通过合并转发的方式，以 PartLength 作为一段的长度
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">文本</param>
    /// <param name="nickname">昵称</param>
    /// <returns></returns>
    public Task<ResponseRootObject> SendLongMessage(string groupId, string text,string nickname)
    {
        var fowardChain = GroupForwardChain.BuildDefault(bot.SelfId.ToString(),nickname,groupId);
        var text_char = text.ToCharArray();

        for (int i = 0; i <= text_char.Length / PartLength; i++)
        {
            int start = i * PartLength;
            int end = (i + 1) * PartLength;

            if (end < text_char.Length)
            {
                fowardChain.AddText(new string(text_char,start, PartLength));
            }
            else
            {
                fowardChain.AddText(new string(text_char, start,text_char.Length-start));
            }
        }
        Act act = new("send_group_forward_msg",fowardChain);
        return _SendAction(act);

    }
    /// <summary>
    /// 发送群AI语音
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">语音的文本</param>
    /// <param name="character">语音角色</param>
    /// <returns></returns>
    public Task<ResponseRootObject> SendGroupAiVoice(string groupId, string text,string character= "lucy-voice-suxinjiejie")
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
    /// <summary>
    /// 获取当前登录账号信息。此信息被BotClient自动获取(SelfId,Nickname属性)，不用重复提取。
    /// </summary>
    /// <returns>(user_id,nickname)</returns>
    public async Task<(long userId,string nickname)> GetAccountInfo()
    {
        Act act = new(
            action: "get_login_info",
            parameters: new object()
        );
        var result=await _SendAction(act);
        var data = result.Data;
        long userId=data.GetProperty("user_id").GetInt64();
        string nickname=data.GetProperty("nickname").GetString();
        return (userId,nickname);
    }
    public async Task<GroupMemberListData> GetGroupMemberListData(string groupId)
    {
        Act act = new(
            action: "get_group_member_list",
            parameters: new { group_id = groupId, no_cache=false }
            );
        var result=await _SendAction(act);
        var data = result.Data;
        return data.Deserialize<GroupMemberListData>();
    }
    /// <summary>
    /// 获取群成员信息
    /// </summary>
    /// <param name="groupId"></param>
    /// <param name="qq"></param>
    /// <returns></returns>
    public async Task<GroupMemberInfo> GetGroupMemberData(string groupId,string qq)
    {
        Act act = new(
            action: "get_group_member_info",
            parameters: new { group_id = groupId, user_id=qq, no_cache = false }
            );
        var result = await _SendAction(act);
        var data = result.Data;
        return data.Deserialize<GroupMemberInfo>();
    }
    /// <summary>
    /// 通过消息ID获取消息
    /// </summary>
    /// <param name="messageId"></param>
    /// <returns></returns>
    public async Task<GroupMessage> GetMessageById(string messageId)
    {
        Act act = new(
            action: "get_msg",
            parameters: new { message_id=messageId }
            );
        var result = await _SendAction(act);
        var data = result.Data;
        var deserilzed= data.Deserialize<GroupMessage>();
        BotUtils.ParseDynamicJsonValue(deserilzed.Message);
        return deserilzed;
    }
    public async Task<ForwardMessage?> GetForwardMessageById(string messageId)
    {
        Act act = new(
            action: "get_forward_msg",
            parameters: new { message_id=messageId }
            );
        var result = await _SendAction(act);
        var data = result.Data;
        var deserilzed= data.Deserialize<ForwardMessage>();
        if (deserilzed == null)
        {
            return null;
        }
        foreach(var msg in deserilzed.Messages)
        {
            BotUtils.ParseDynamicJsonValue(msg.Message);
        }
        return deserilzed;
        
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
