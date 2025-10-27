using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("锐言锐语","使用/hr来获取")]
public class HeruiSaying :Plugin
{
    private const string url = "https://the-brotherhood-of-scu.github.io/herui_saying_text/";
    private List<string> sayings = new List<string>();
    private readonly ThreadLocal<Random> _randomWrapper = new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));
    public HeruiSaying(PluginInterop interop):base(interop)
    {
        AutoUpdate();
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        if (!IsStartsWith(chain, "/hr"))
        {
            return;
        }
        _=Actions.SendGroupMessage(groupId, PickOne());
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
    private async void AutoUpdate()
    {
        while (true) {
            await Update();
            Logger.Info("data loaded");
            await Task.Delay(1000*60*60);//update every 1 hour
        }
    }
    private async Task Update()
    {
        var text=await HttpGetAsync(url);
        if (text == null) { 
            return;
        }
        var strings = text.Split('\n',StringSplitOptions.RemoveEmptyEntries);
        sayings=new List<string>(strings);
    }
    
    private readonly HttpClient _httpClient = new HttpClient();
    public async Task<string?> HttpGetAsync(string url)
    {
        try
        {
            // 发送GET请求
            HttpResponseMessage response = await _httpClient.GetAsync(url);
            // 确保HTTP响应状态为成功
            response.EnsureSuccessStatusCode();
            // 读取响应内容
            string responseBody = await response.Content.ReadAsStringAsync();
            return responseBody;
        }
        catch (Exception e)
        {
            // 处理请求异常
            Logger.Warn($"update failed due to {e.Message}");
            return null;
        }
    }
}
