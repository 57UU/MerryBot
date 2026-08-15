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
    public int TokenUsed { get; internal set; } = -1;//-1 is unknown
    public Context Fork()
    {
        return new Context([.. Messages]) { TokenUsed = TokenUsed };
    }
}
