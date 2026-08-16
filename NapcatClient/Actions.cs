using CommonLib;
using NapcatClient.MessageType;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Websocket.Client;

namespace NapcatClient.Action;

public class Actions
{
    private readonly ISimpleLogger Logger;
    private readonly WebSocketAdapter adapter;
    public long? SelfId { get; private set; }
    public string? Nickname { get; private set; }

    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly RequestCaching requestCaching = new(TimeSpan.FromMinutes(1));
    private readonly ConcurrentDictionary<string, object> _inflightRequests = new();

    private Task<T> HttpGetCached<T>(string url, string prefix, Func<HttpContent, Task<T>> readContent)
    {
        var cacheKey = $"{prefix}-{url}";
        if (requestCaching.TryGetCache(cacheKey, out T? cacheRes))
            return Task.FromResult(cacheRes!);

        var lazy = (Lazy<Task<T>>)_inflightRequests.GetOrAdd(cacheKey, _ => new Lazy<Task<T>>(async () =>
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var result = await readContent(response.Content);
                requestCaching.SetCache(cacheKey, result);
                return result;
            }
            finally
            {
                _inflightRequests.TryRemove(cacheKey, out var _);
            }
        }));
        return lazy.Value;
    }

    public Task<byte[]> HttpGetBinary(string url) => HttpGetCached(url, "http-bin", c => c.ReadAsByteArrayAsync());
    public Task<string> HttpGetText(string url) => HttpGetCached(url, "http-text", c => c.ReadAsStringAsync());

    public Actions(ISimpleLogger logger, WebSocketAdapter adapter)
    {
        Logger = logger;
        this.adapter = adapter;
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseRootObject>> _pendingResponses = new();
    private long _echoCounter = 0;
    public Task<ResponseRootObject> _SendAction(ParameteredAct act, string? cacheKey = null, TimeSpan? expiration = null)
    {
        return _SendAction(act.ToAct(), cacheKey, expiration);
    }
    private static string ConstraintLength(string s, int lengthConstraint = 1000, string prompt = "...")
    {
        if (s.Length > lengthConstraint)
        {
            s = string.Concat(s.AsSpan(0, lengthConstraint), prompt);
        }
        return s;
    }
    public async Task<ResponseRootObject> _SendAction(Act act, string? cacheKey = null, TimeSpan? expiration = null)
    {
        if (cacheKey != null && requestCaching.TryGetCache(cacheKey, out ResponseRootObject? cacheRes))
        {
            return cacheRes!;
        }

        var echo = Interlocked.Increment(ref _echoCounter).ToString();
        act.Echo = echo;

        var tcs = new TaskCompletionSource<ResponseRootObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingResponses.TryAdd(echo, tcs))
        {
            throw new InvalidOperationException($"Duplicate echo: {echo}");
        }

        try
        {
            var json = BotUtils.Serialize(act);
            Logger.Debug($"sending: {ConstraintLength(json)}]");

            // 未连接时 SendAsync 抛异常；发送为同步写流，保留 Task.Run 以避免阻塞调用线程
            await adapter.SendAsync(json);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var result = await tcs.Task;

                // 仅缓存成功响应，失败响应不写入缓存，避免短暂故障被缓存放大
                if (cacheKey != null && result.Status == "ok")
                {
                    requestCaching.SetCache(cacheKey, result, expiration);
                }

                return result;
            }
        }
        finally
        {
            _pendingResponses.TryRemove(echo, out _);
        }
    }
    internal void AddResponse(string echo, ResponseRootObject response)
    {
        Logger.Trace($"return: {echo}");

        if (_pendingResponses.TryRemove(echo, out var tcs))
        {
            tcs.TrySetResult(response);
        }
        else
        {
            Logger.Warn($"Received response for unknown echo: {echo}");
        }
    }
    /// <summary>
    /// 在QQ群中发送消息
    /// </summary>
    /// <param name="groupId">qq群号</param>
    /// <param name="messageChain">消息链</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> SendGroupMessage(long groupId, IEnumerable<TypedMessage> messageChain)
    {
        Dictionary<string, dynamic> parameters = new();
        parameters["group_id"] = groupId;
        parameters["message"] = messageChain;
        ParameteredAct act = new(
            "send_group_msg",
            parameters
            );
        return await _SendAction(act);
    }
    /// <summary>
    /// 获取空的消息链
    /// </summary>
    public static List<TypedMessage> EmptyMessageChain => new List<TypedMessage>();
    /// <summary>
    /// 在QQ群中发送文本消息
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> SendGroupMessage(long groupId, string text)
    {
        List<TypedMessage> messages = new List<TypedMessage>();
        messages.Add(TextData.FromText(text));
        return await SendGroupMessage(groupId, messages);
    }
    /// <summary>
    /// 在QQ群中回复一条消息
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="messageId">要回复的消息的ID</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> ReplyGroupMessage(long groupId, long messageId, string text)
    {
        List<TypedMessage> messages = new List<TypedMessage>();
        messages.Add(ReplyData.FromReply(messageId.ToString()));
        messages.Add(TextData.FromText(text));
        return await SendGroupMessage(groupId, messages);
    }
    public async Task<ResponseRootObject> ReplyGroupMessageWithMention(long groupId, string qq, string text)
    {
        List<TypedMessage> messages = [
            AtData.FromAt(qq),
            TextData.FromText(" "+text)
        ];
        return await SendGroupMessage(groupId, messages);
    }
    public int PartLength { set; get; } = 500;
    private const int LineSearchRadius = 10;
    public string DefaultNickname { get; set; } = "曼瑞";
    /// <summary>
    /// 在QQ群中选择最合适的回复方式（长：转发消息；短：直接回复）
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="messageId">要回复的消息的ID</param>
    /// <param name="text">文本</param>
    /// <returns></returns>
    public Task<ResponseRootObject> ChooseBestReplyMethod(long groupId, string qq, string text)
    {
        return ChooseBestReplyMethod(groupId, qq, text, DefaultNickname);
    }
    /// <summary>
    /// 在QQ群中选择最合适的回复方式（长：转发消息；短：直接回复）
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="qq">target qq</param>
    /// <param name="text">文本</param>
    /// <param name="nickname">昵称</param>
    /// <returns></returns>
    public Task<ResponseRootObject> ChooseBestReplyMethod(long groupId, string qq, string text, string nickname)
    {
        if (text.Length > PartLength)
        {
            return SendLongMessage(groupId.ToString(), text, nickname);
        }
        else
        {
            return ReplyGroupMessageWithMention(groupId, qq, text);
        }
    }
    private async Task<long> getSelfId()
    {
        if (SelfId.HasValue)
        {
            return SelfId.Value;
        }
        else
        {
            var result = await GetAccountInfo();
            SelfId = result.userId;
            Nickname = result.nickname;
            return SelfId.Value;
        }
    }
    /// <summary>
    /// 发送长消息，通过合并转发的方式，以 PartLength 作为一段的长度
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">文本</param>
    /// <param name="nickname">昵称</param>
    /// <returns></returns>
    public async Task<ResponseRootObject> SendLongMessage(string groupId, string text, string nickname)
    {
        var fowardChain = new GroupForwardChain.Builder((await getSelfId()).ToString(), nickname, groupId);

        int i = 0;
        while (i < text.Length)
        {
            int end = Math.Min(i + PartLength, text.Length);

            if (end < text.Length)
            {
                int cut = end;
                int searchStart = Math.Max(i, end - LineSearchRadius);
                int searchEnd = Math.Min(text.Length, end + LineSearchRadius);
                for (int j = end - 1; j >= searchStart; j--)
                {
                    if (text[j] == '\n')
                    {
                        cut = j + 1;
                        break;
                    }
                }
                if (cut == end)
                {
                    for (int j = end; j < searchEnd; j++)
                    {
                        if (text[j] == '\n')
                        {
                            cut = j;
                            break;
                        }
                    }
                }
                fowardChain.AddText(text[i..cut]);
                i = cut;
            }
            else
            {
                fowardChain.AddText(text[i..]);
                break;
            }
        }
        Act act = new("send_group_forward_msg", fowardChain.Build());
        return await _SendAction(act);

    }
    /// <summary>
    /// 发送群AI语音
    /// </summary>
    /// <param name="groupId">QQ群号</param>
    /// <param name="text">语音的文本</param>
    /// <param name="character">语音角色</param>
    /// <returns></returns>
    public Task<ResponseRootObject> SendGroupAiVoice(string groupId, string text, string character = "lucy-voice-suxinjiejie")
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
    public async Task<(long userId, string nickname)> GetAccountInfo()
    {
        Act act = new(
            action: "get_login_info",
            parameters: new object()
        );
        var result = await _SendAction(act);
        var data = result.Data;
        long userId = data.GetProperty("user_id").GetInt64();
        string nickname = data.GetProperty("nickname").GetString()!;
        return (userId, nickname);
    }
    public async Task<GroupInfo> GetGroupInfo(string groupId)
    {
        Act act = new(
            action: "get_group_info",
            parameters: new { group_id = groupId }
            );
        var result = await _SendAction(act, $"group_info_{groupId}");
        var data = result.Data;
        return BotUtils.Deserialize<GroupInfo>(data)!;
    }
    public async Task<GroupMemberListData> GetGroupMemberListData(string groupId)
    {
        Act act = new(
            action: "get_group_member_list",
            parameters: new { group_id = groupId, no_cache = false }
            );
        var result = await _SendAction(act, $"group_member_list_{groupId}");
        var data = result.Data;
        return BotUtils.Deserialize<GroupMemberListData>(data)!;
    }
    /// <summary>
    /// 获取群成员信息
    /// </summary>
    /// <param name="groupId"></param>
    /// <param name="qq"></param>
    /// <returns></returns>
    public async Task<GroupMemberInfo?> GetGroupMemberData(string groupId, string qq)
    {
        Act act = new(
            action: "get_group_member_info",
            parameters: new { group_id = groupId, user_id = qq, no_cache = false }
            );
        var result = await _SendAction(act, $"group_member_info_{groupId}_{qq}");
        if (result.Status == "failed")
        {
            return null;
        }
        var data = result.Data;
        return BotUtils.Deserialize<GroupMemberInfo>(data);
    }
    /// <summary>
    /// 通过消息ID获取消息
    /// </summary>
    /// <param name="messageId"></param>
    /// <returns></returns>
    public async Task<GroupMessage?> GetMessageById(string messageId)
    {
        Act act = new(
            action: "get_msg",
            parameters: new { message_id = messageId }
            );
        var result = await _SendAction(act, $"get_msg_{messageId}");
        var data = result.Data;
        var deserilzed = BotUtils.Deserialize<GroupMessage>(data);
        if (deserilzed == null)
        {
            return null;
        }
        return deserilzed;
    }
    public async Task<ForwardMessage?> GetForwardMessageById(string messageId)
    {
        Act act = new(
            action: "get_forward_msg",
            parameters: new { message_id = messageId }
            );
        var result = await _SendAction(act, $"get_forward_msg_{messageId}");
        var data = result.Data;
        var deserilzed = BotUtils.Deserialize<ForwardMessage>(data);
        if (deserilzed == null)
        {
            return null;
        }
        return deserilzed;

    }


}
public class Act
{
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
        var tmp = new Act(this.Action, this.Parameters);
        tmp.Echo = this.Echo;
        return tmp;
    }

}
