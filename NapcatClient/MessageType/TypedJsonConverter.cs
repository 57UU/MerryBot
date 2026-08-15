
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NapcatClient.MessageType;

class TypedJsonConverter : JsonConverter<TypedMessage>
{
    private TypedJsonConverter() { }
    public static TypedJsonConverter Instance { get; } = new();
    public override TypedMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using (var jsonDocument = JsonDocument.ParseValue(ref reader))
        {
            var root = jsonDocument.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                // 缺少 type 字段：返回安全兜底文本段，绝不抛异常中断整条消息链
                return TextData.FromText(string.Empty);
            }

            string type = typeElement.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(type))
            {
                return TextData.FromText(string.Empty);
            }

            // 检查是否有 data 属性
            if (!root.TryGetProperty("data", out var dataElement))
            {
                return TextData.FromText(string.Empty);
            }

            // 使用 data 部分进行反序列化
            string dataJson = dataElement.GetRawText();
            return type switch
            {
                MessageTypeStringStr.Text => JsonSerializer.Deserialize<TextData>(dataJson, options),
                MessageTypeStringStr.Image => JsonSerializer.Deserialize<ImageData>(dataJson, options),
                MessageTypeStringStr.File => JsonSerializer.Deserialize<FileData>(dataJson, options),
                MessageTypeStringStr.At => JsonSerializer.Deserialize<AtData>(dataJson, options),
                MessageTypeStringStr.Reply => JsonSerializer.Deserialize<ReplyData>(dataJson, options),
                MessageTypeStringStr.Face => JsonSerializer.Deserialize<FaceData>(dataJson, options),
                MessageTypeStringStr.Dice => JsonSerializer.Deserialize<DiceData>(dataJson, options),
                MessageTypeStringStr.Rps => JsonSerializer.Deserialize<RpsData>(dataJson, options),
                MessageTypeStringStr.Poke => JsonSerializer.Deserialize<PokeData>(dataJson, options),
                MessageTypeStringStr.Forward => JsonSerializer.Deserialize<ForwardData>(dataJson, options),
                MessageTypeStringStr.Mface => JsonSerializer.Deserialize<MfaceData>(dataJson, options),
                MessageTypeStringStr.Record => JsonSerializer.Deserialize<RecordData>(dataJson, options),
                MessageTypeStringStr.Video => JsonSerializer.Deserialize<VideoData>(dataJson, options),
                MessageTypeStringStr.Json => JsonSerializer.Deserialize<JsonData>(dataJson, options),
                MessageTypeStringStr.Music => JsonSerializer.Deserialize<MusicData>(dataJson, options),
                // 未知或暂不支持的类型（如 contact、xml 等）：返回兜底文本段，不抛异常
                _ => TextData.FromText($"[{type}]")
            };
        }
    }

    public override void Write(Utf8JsonWriter writer, TypedMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // 确定消息类型
        string type = value switch
        {
            TextData => MessageTypeStringStr.Text,
            ImageData => MessageTypeStringStr.Image,
            FileData => MessageTypeStringStr.File,
            AtData => MessageTypeStringStr.At,
            ReplyData => MessageTypeStringStr.Reply,
            FaceData => MessageTypeStringStr.Face,
            DiceData => MessageTypeStringStr.Dice,
            RpsData => MessageTypeStringStr.Rps,
            PokeData => MessageTypeStringStr.Poke,
            ForwardData => MessageTypeStringStr.Forward,
            RecordData => MessageTypeStringStr.Record,
            VideoData => MessageTypeStringStr.Video,
            JsonData => MessageTypeStringStr.Json,
            _ => throw new JsonException($"Unknown message type: {value.GetType().Name}")
        };

        writer.WriteString("type", type);
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, value, value.GetType(), options);

        writer.WriteEndObject();
    }
}
