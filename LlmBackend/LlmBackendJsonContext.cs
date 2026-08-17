using System.Text.Json.Serialization;

namespace LlmBackend;

/// <summary>
/// LlmBackend 的 STJ source generator 上下文（NativeAOT 兼容）。
/// 注册所有 backend 的响应 DTO 与工具定义类型。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    IncludeFields = true)]
[JsonSerializable(typeof(ToolDef))]
[JsonSerializable(typeof(FunctionDef))]
[JsonSerializable(typeof(List<ToolDef>))]
[JsonSerializable(typeof(BackendErrors.ApiErrorEnvelope))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionStreamChunk))]
[JsonSerializable(typeof(AnthropicResponse))]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(ThinkingBlock))]
[JsonSerializable(typeof(List<ThinkingBlock>))]
[JsonSerializable(typeof(ResponsesResponse))]
[JsonSerializable(typeof(ResponsesStreamEvent))]
internal sealed partial class LlmBackendJsonContext : JsonSerializerContext
{
}
