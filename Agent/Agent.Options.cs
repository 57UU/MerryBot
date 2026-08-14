using LlmClient;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agent;

public class AgentOptions
{
    public string SystemPrompt { get; set; } = "You are a helpful assistant.";
    public int MaxIterations { get; set; } = 10;
    public double ContextCompactRatio { get; set; } = 0.7;
    public Client? ImageInterpreter;

}
