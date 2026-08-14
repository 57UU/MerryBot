using LlmClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agent;

public class AgentOptions
{
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public int MaxIterations { get; set; } = 20;
    public double ContextCompactRatio { get; set; } = 0.7;
    public int? MaxOutputTokens { get; set; }
    /// <summary>
    /// Optional, best-effort lifecycle callback. Exceptions raised by the
    /// callback are ignored so observability can never interrupt a chat.
    /// </summary>
    public Action<AgentLogEvent>? OnLog { get; set; }
}
