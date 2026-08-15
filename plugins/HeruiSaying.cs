using NapcatClient;
using NapcatClient.MessageType;

namespace BotPlugin;

[PluginTag("herui-saying", "锐言锐语", "使用/hr来获取", isIgnore: true)]
public class HeruiSaying : Plugin
{
    private const string url = "https://the-brotherhood-of-scu.github.io/herui_saying_text/";
    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private List<string> sayings = new List<string>();
    private readonly ThreadLocal<Random> _randomWrapper = new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));
    private readonly CancellationTokenSource _updateCts = new();
    public HeruiSaying(PluginInterop interop) : base(interop)
    {
        _ = AutoUpdateAsync();
    }
    public override Task OnGroupMessageAsync(bool isMentioned, Command? command, IReadOnlyList<TypedMessage> messageChain, ReceivedGroupMessage data)
    {
        if (!isMentioned || command?.Name != "hr")
        {
            return Task.CompletedTask;
        }
        _ = Channel.SendGroupMessage(data.GroupId, PickOne());
        return Task.CompletedTask;
    }
    private string PickOne()
    {
        if (sayings.Count == 0)
        {
            return "暂未获取到数据，请稍后再试";
        }
        int index = _randomWrapper.Value!.Next(sayings.Count);
        return $"{sayings[index]}\n--Herui--[{index}/{sayings.Count}]";
    }
    private async Task AutoUpdateAsync()
    {
        while (!_updateCts.IsCancellationRequested)
        {
            try
            {
                await Update();
                Logger.Info("data loaded");
            }
            catch (Exception ex)
            {
                //循环内异常只记日志，保证定时更新不中断
                Logger.Warn($"herui saying update failed: {ex.Message}");
            }
            try
            {
                await Task.Delay(1000 * 60 * 60, _updateCts.Token);//update every 1 hour
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    public override void Dispose()
    {
        _updateCts.Cancel();
        _updateCts.Dispose();
        base.Dispose();
    }
    private async Task Update()
    {
        var text = await HttpGetAsync(url);
        if (text == null)
        {
            return;
        }
        var strings = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sayings = new List<string>(strings);
    }

    public async Task<string?> HttpGetAsync(string url)
    {
        try
        {
            return await httpClient.GetStringAsync(url);
        }
        catch (Exception e)
        {
            // 处理请求异常
            Logger.Warn($"update failed due to {e.Message}");
            return null;
        }
    }
}
