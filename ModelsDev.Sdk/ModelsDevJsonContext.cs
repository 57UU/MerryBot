using System.Text.Json.Serialization;
using ModelsDev.Sdk.Models;

namespace ModelsDev.Sdk;

/// <summary>
/// ModelsDev.Sdk 的 STJ source generator 上下文（NativeAOT 兼容）。
/// 注册 models.dev API 响应的模型类型。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Provider))]
[JsonSerializable(typeof(Dictionary<string, Provider>))]
[JsonSerializable(typeof(ModelInfo))]
internal sealed partial class ModelsDevJsonContext : JsonSerializerContext
{
}
