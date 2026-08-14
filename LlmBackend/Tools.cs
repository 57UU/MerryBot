using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LlmBackend;

public class ToolDef
{
    public string type;
    public FunctionDef function;
}

public class FunctionDef
{
    public string name;
    public string description;
    public JsonElement? parameters;
}
