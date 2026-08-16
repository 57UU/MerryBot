using LlmBackend;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agent;

public class Context
{
    public Context(IList<Message>? messages = null)
    {
        this.Messages = messages ?? new List<Message>();
    }
    public IList<Message> Messages { get; set; }
    public int TokenUsed { get; internal set; } = -1;//-1 未知；有值 = 最后一次 LLM 请求的 输入+输出 token 数（当前上下文占用，压缩判断用）
    public Context Fork()
    {
        return new Context([.. Messages]) { TokenUsed = TokenUsed };
    }
}
