using System.Net.Http.Headers;

namespace LlmBackend;

/// <summary>
/// LLM 请求的客户端标识头 <c>User-Agent: MerryBot/1.0</c>。
/// 主流 Agent 工具（GPTBot/ClaudeBot）的标准做法：直接表明机器人身份，
/// 便于 Provider/中继做客户端统计、限流与问题排查；与 x-opencode-session
/// 亲和头正交，三个后端（OpenAI 兼容/Anthropic/Responses）统一发送。
/// 取值以 <see cref="LlmDefaults.UserAgent"/> 为唯一来源。
/// </summary>
internal static class LlmUserAgent
{
    /// <summary>
    /// 把 UA 追加到请求上；调用方已显式设置 UA 时不覆盖（显式值优先）。
    /// </summary>
    internal static void ApplyUserAgent(HttpRequestMessage request)
    {
        if (request.Headers.UserAgent.Count != 0)
        {
            return;
        }
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(LlmDefaults.UserAgent));
    }
}
