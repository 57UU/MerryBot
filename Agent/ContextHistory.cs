using LlmBackend;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agent;

public interface ContextHistory
{
    public Task<IList<Message>> Restore();
    public Task Append(IList<Message> messages);
    public Task Replace(IList<Message> messages);
    public Task Clear();
}
