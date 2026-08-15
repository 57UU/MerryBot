namespace MerryBot.WebUI.Components.Shared;

/// <summary>
/// 消息渲染形式：
/// Inline = 群聊消息页（语音/视频内嵌播放、转发消息打开模态框）；
/// Link = 转发消息详情页（语音/视频/转发消息均为链接跳转）。
/// </summary>
public enum MessageRenderMode
{
    Inline,
    Link,
}
