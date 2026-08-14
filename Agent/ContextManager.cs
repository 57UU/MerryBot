using LlmBackend;

namespace Agent;

public class ContextManager
{
    public ContextHistory contextHistory { get; private set; }
    public Context context { get; private set; }
    public readonly int TokenLimit;
    /// <summary>
    /// 上下文使用比例, negative means unavailable
    /// </summary>
    public double ContextRatio => (double)context.TokenUsed / TokenLimit;
    private ContextManager(
        ContextHistory contextHistory,
        Context context,
        int tokenLimit
        )
    {
        this.TokenLimit = tokenLimit;
        this.contextHistory = contextHistory;
        this.context = context;
        context.TokenUsed = TokenLimit;
    }
    public static async Task<ContextManager> Create(
        ContextHistory contextHistory,
        int tokenLimit
        )
    {
        var history = await contextHistory.Restore();
        Context context = new(history);
        return
        new ContextManager(contextHistory, context, tokenLimit);
    }
    public async Task Compact(CancellationToken cancellationToken,
        Func<CancellationToken, Context, Task<(string result, TokenUsage tokenUsage)>> compactFunc
    )
    {
        var (compactedText, tokenUsage) = await compactFunc(cancellationToken, context);

        // 压缩返回空摘要时视为失败，保留原上下文
        if (string.IsNullOrWhiteSpace(compactedText))
        {
            return;
        }

        // 压缩后上下文仅保留摘要，用量重置为生成摘要的消耗（completion），
        // 而非整段旧上下文重新发送的消耗（total），避免压缩后比例仍然虚高反复触发压缩
        context.Messages = [Message.User(compactedText)];
        context.TokenUsed = tokenUsage.completionUsage;

        await contextHistory.Replace(context.Messages);
    }


}