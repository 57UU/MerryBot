namespace LlmBackend;

/// <summary>
/// OpenCode 中继（opencode.ai Zen/Go/free）的会话亲和头 <c>x-opencode-session</c>。
/// OpenCode 把携带相同头值的请求 pin 到同一上游 backend，使同一会话的 prompt cache 保持温热；
/// 头值只需 per-会话不透明稳定，不携带个人数据。
/// 决议时机为后端构造期：传入 sessionKey 则原样使用，未传入则为该后端实例生成一个稳定随机数
/// （readonly 字段，跨 Generate/GenerateStream/重试不变）；非 OpenCode 目标不发送。
/// </summary>
internal static class OpenCodeAffinity
{
    internal const string SessionHeaderName = "x-opencode-session";

    /// <summary>
    /// 是否为 OpenCode 中继目标：baseUrl 的 host 为 opencode.ai 或其子域（大小写不敏感）。
    /// </summary>
    internal static bool IsOpenCodeTarget(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }
        var host = uri.Host;
        return host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 决议后端实例终身使用的会话 key：非 OpenCode 目标返回 null（不发送）；
    /// 目标 + 传入非空则原样返回；目标 + 未传入则生成稳定随机数（调用方应在构造期调用一次并缓存）。
    /// </summary>
    internal static string? ResolveSessionKey(string? configuredSessionKey, string? baseUrl)
    {
        if (!IsOpenCodeTarget(baseUrl))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(configuredSessionKey))
        {
            return configuredSessionKey;
        }
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// key 非空时把亲和头追加到请求上；已存在的同名头不覆盖（调用方显式值优先）。
    /// </summary>
    internal static void ApplySessionHeader(HttpRequestMessage request, string? sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey) || request.Headers.Contains(SessionHeaderName))
        {
            return;
        }
        request.Headers.TryAddWithoutValidation(SessionHeaderName, sessionKey);
    }
}
