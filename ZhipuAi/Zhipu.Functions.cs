using System;
using System.Collections.Generic;
using System.Text;

namespace ZhipuClient;

public partial class ZhipuAi
{
    private void RegisterGetTime()
    {
        var watch = new ToolDef();
        watch.Function.Name = "get_time";
        watch.Function.Description = "查看现在的时间";
        watch.Function.FunctionCall = async (parameters) => "北京时间:" + DateTime.Now.ToString();
        RegisterTool(watch);
    }
    private void RegisterBrowser()
    {
        var browserDef = new ToolDef();
        browserDef.Function.Name = "browse";
        browserDef.Function.Description = "浏览网页";
        browserDef.Function.Parameters.AddRequired("url", new ParameterProperty() { Type = "string", Description = "需要访问的网址" });
        browserDef.Function.FunctionCall = async (parameters) =>
        {
            var url = parameters["url"];
            var html = await browser.View(url.GetString()!);
            if (html.Length > MaxWebContentLength)
            {
                html = string.Concat(html.AsSpan(0, MaxWebContentLength), "[省略过长内容]");
            }
            return html;
        };
        RegisterTool(browserDef);
    }
    private void RegisterBingSearch()
    {
        var bingSearch = new ToolDef();
        bingSearch.Function.Name = "search";
        bingSearch.Function.Description = "使用Bing搜索";
        bingSearch.Function.Parameters.AddRequired("query", new ParameterProperty() { Type = "string", Description = "keyword" });
        bingSearch.Function.Parameters.AddNonRequired("internationalVersion", new ParameterProperty() { Type = "boolean", Description = "是否启用国际版" });
        bingSearch.Function.FunctionCall = async (parameters) =>
        {
            var query = parameters["query"];
            var internationalVersion = false;
            if (parameters.TryGetValue("internationalVersion", out var v))
            {
                internationalVersion = v.GetBoolean();
            }
            var result = await browser.Search(query.GetString()!, internationalVersion);
            return result;
        };
        bingSearch.DynamicPrompt = "网络搜索时，优先使用国内版。";
        RegisterTool(bingSearch);
    }
    private void RegisterWeiboHot()
    {
        var weiboHot = new ToolDef();
        weiboHot.Function.Name = "view_weibo_hot";
        weiboHot.Function.Description = "查看微博热搜";
        weiboHot.Function.FunctionCall = async (parameters) => await browser.GetWeiboHot();
        RegisterTool(weiboHot);
    }
}
