using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LlmBackend;

public class ToolDef
{
    public string type = string.Empty;
    public FunctionDef function = new();
}

public class FunctionDef
{
    public string name = string.Empty;
    public string description = string.Empty;
    public JsonElement? parameters;
}
