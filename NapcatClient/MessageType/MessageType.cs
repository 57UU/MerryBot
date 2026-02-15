using System.Text.Json;
using System.Text.Json.Serialization;

namespace NapcatClient.MessageType;

/// <summary>
/// 消息类型基类
/// view https://napneko.github.io/onebot/sement for details
/// </summary>
public abstract class TypedMessage
{
    public abstract TypedMessage Clone();
}

/// <summary>
/// 文本消息数据
/// 用于发送纯文本内容
/// </summary>
public class TextData : TypedMessage
{
    /// <summary>
    /// 要发送的文本内容
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; }

    public static TextData FromText(string text) => new TextData { Text = text };

    public override TypedMessage Clone()
    {
        return new TextData { Text = Text };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (TextData)obj;
        return Text == other.Text;
    }
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>
/// @提及数据
/// 用于在消息中 @ 特定用户或全体成员
/// </summary>
public class AtData : TypedMessage
{
    /// <summary>
    /// 要 @ 的 QQ 号，使用 "all" 表示 @全体成员
    /// </summary>
    [JsonPropertyName("qq")]
    public string Qq { get; set; }

    public static AtData FromAt(string qq) => new AtData { Qq = qq };

    public override TypedMessage Clone()
    {
        return new AtData { Qq = Qq };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (AtData)obj;
        return Qq == other.Qq;
    }
    public override string ToString()
    {
        return $"@{Qq}";
    }
}

/// <summary>
/// 回复数据
/// 用于回复特定消息
/// </summary>
public class ReplyData : TypedMessage
{
    /// <summary>
    /// 被回复消息的唯一 ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    public static ReplyData FromReply(string id) => new ReplyData { Id = id };

    public override TypedMessage Clone()
    {
        return new ReplyData { Id = Id };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (ReplyData)obj;
        return Id == other.Id;
    }
    public override string ToString()
    {
        return $"reply {Id}";
    }
}

/// <summary>
/// QQ 表情数据
/// 用于发送QQ内置表情
/// </summary>
public class FaceData : TypedMessage
{
    /// <summary>
    /// 表情 ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// 表情原始数据
    /// </summary>
    [JsonPropertyName("raw")]
    public JsonElement? Raw { get; set; }

    /// <summary>
    /// 骰子或石头剪刀布结果 ID
    /// </summary>
    [JsonPropertyName("resultId")]
    public string? ResultId { get; set; }

    /// <summary>
    /// 连续发送次数
    /// </summary>
    [JsonPropertyName("chainCount")]
    public int? ChainCount { get; set; }

    public override TypedMessage Clone()
    {
        return new FaceData { Id = Id, Raw = Raw, ResultId = ResultId, ChainCount = ChainCount };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (FaceData)obj;
        return Id == other.Id && ResultId == other.ResultId && ChainCount == other.ChainCount && object.Equals(Raw, other.Raw);
    }
    public override string ToString()
    {
        return $"face {ToChinese()}";
    }
    public string ToChinese(){
        return QqFace.GetFace(int.Parse(Id));
    }
}

/// <summary>
/// 商城表情数据
/// 用于发送QQ商城表情
/// </summary>
public class MfaceData : TypedMessage
{
    /// <summary>
    /// 表情 ID
    /// </summary>
    [JsonPropertyName("emoji_id")]
    public string EmojiId { get; set; }

    /// <summary>
    /// 表情包 ID
    /// </summary>
    [JsonPropertyName("emoji_package_id")]
    public string EmojiPackageId { get; set; }

    /// <summary>
    /// 表情 key
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>
    /// 表情名称
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    public override TypedMessage Clone()
    {
        return new MfaceData { EmojiId = EmojiId, EmojiPackageId = EmojiPackageId, Key = Key, Summary = Summary };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (MfaceData)obj;
        return EmojiId == other.EmojiId && EmojiPackageId == other.EmojiPackageId && Key == other.Key && Summary == other.Summary;
    }
    public override string ToString()
    {
        return $"mface {EmojiId}";
    }
}

/// <summary>
/// 骰子表情数据
/// 用于发送骰子表情
/// </summary>
public class DiceData : TypedMessage
{
    /// <summary>
    /// 骰子点数结果(1-6)
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; set; }

    public override TypedMessage Clone()
    {
        return new DiceData { Result = Result };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (DiceData)obj;
        return Result == other.Result;
    }
    public override string ToString()
    {
        return $"dice {Result}";
    }
}

/// <summary>
/// 石头剪刀布数据
/// 用于发送石头剪刀布表情
/// </summary>
public class RpsData : TypedMessage
{
    /// <summary>
    /// 石头剪刀布结果(1-3，分别代表石头、剪刀、布)
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; set; }

    public override TypedMessage Clone()
    {
        return new RpsData { Result = Result };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (RpsData)obj;
        return Result == other.Result;
    }
    public override string ToString()
    {
        return $"rps {Result}";
    }
}

/// <summary>
/// 戳一戳数据
/// 用于发送戳一戳消息
/// </summary>
public class PokeData : TypedMessage
{
    /// <summary>
    /// 戳一戳类型
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; }

    /// <summary>
    /// 戳一戳 ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    public override TypedMessage Clone()
    {
        return new PokeData { Type = Type, Id = Id };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (PokeData)obj;
        return Type == other.Type && Id == other.Id;
    }
    public override string ToString()
    {
        return $"poke {Type} {Id}";
    }
}

/// <summary>
/// 图片消息数据
/// 用于发送图片
/// </summary>
public class ImageData : TypedMessage
{
    /// <summary>
    /// 图片文件路径、URL 或 Base64 编码
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; }

    /// <summary>
    /// 图片 URL
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 图片描述
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>
    /// 图片子类型
    /// </summary>
    [JsonPropertyName("sub_type")]
    public int? SubType { get; set; }

    /// <summary>
    /// 文件大小(字节)
    /// </summary>
    [JsonIgnore]
    public long? FileSize { get; set; }

    [JsonPropertyName("file_size")]
    public string? FileSizeStr
    {
        get => FileSize?.ToString();
        set => FileSize = value != null ? long.Parse(value) : default;
    }

    /// <summary>
    /// 表情 key（当为商城表情转换而来时）
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>
    /// 表情 ID（当为商城表情转换而来时）
    /// </summary>
    [JsonPropertyName("emoji_id")]
    public string? EmojiId { get; set; }

    /// <summary>
    /// 表情包 ID（当为商城表情转换而来时）
    /// </summary>
    [JsonPropertyName("emoji_package_id")]
    public string? EmojiPackageId { get; set; }

    public override TypedMessage Clone()
    {
        return new ImageData { File = File, Url = Url, Summary = Summary, SubType = SubType, FileSize = FileSize, Key = Key, EmojiId = EmojiId, EmojiPackageId = EmojiPackageId };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (ImageData)obj;
        return File == other.File && Url == other.Url && Summary == other.Summary && SubType == other.SubType && FileSize == other.FileSize && Key == other.Key && EmojiId == other.EmojiId && EmojiPackageId == other.EmojiPackageId;
    }
    public override string ToString()
    {
        return $"image {File}";
    }
}

/// <summary>
/// 语音消息数据
/// 用于发送语音消息
/// </summary>
public class RecordData : TypedMessage
{
    /// <summary>
    /// 语音文件名
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; }

    /// <summary>
    /// 语音 URL
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }

    /// <summary>
    /// 文件大小(字节)
    /// </summary>
    [JsonIgnore]
    public long? FileSize { get; set; }

    [JsonPropertyName("file_size")]
    public string? FileSizeStr
    {
        get => FileSize?.ToString();
        set => FileSize = value != null ? long.Parse(value) : default;
    }

    /// <summary>
    /// 文件路径
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    public override TypedMessage Clone()
    {
        return new RecordData { File = File, Url = Url, FileSize = FileSize, Path = Path };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (RecordData)obj;
        return File == other.File && FileSize == other.FileSize && Path == other.Path;
    }
    public override string ToString()
    {
        return $"record {File}";
    }
}

/// <summary>
/// 视频消息数据
/// 用于发送视频
/// </summary>
public class VideoData : TypedMessage
{
    /// <summary>
    /// 视频文件路径、URL 或 Base64 编码
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; }

    /// <summary>
    /// 视频在线 URL
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 文件大小(字节)
    /// </summary>
    [JsonIgnore]
    public long? FileSize { get; set; }

    [JsonPropertyName("file_size")]
    public string? FileSizeStr
    {
        get => FileSize?.ToString();
        set => FileSize = value != null ? long.Parse(value) : default;
    }

    /// <summary>
    /// 视频缩略图
    /// </summary>
    [JsonPropertyName("thumb")]
    public string? Thumb { get; set; }

    public override TypedMessage Clone()
    {
        return new VideoData { File = File, Url = Url, FileSize = FileSize, Thumb = Thumb };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (VideoData)obj;
        return File == other.File && Url == other.Url && FileSize == other.FileSize && Thumb == other.Thumb;
    }
    public override string ToString()
    {
        return $"video {File}";
    }
}

/// <summary>
/// 文件消息数据
/// 用于发送文件
/// </summary>
public class FileData : TypedMessage
{
    /// <summary>
    /// 文件名
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; }
    /// <summary>
    /// url
    /// </summary>

    [JsonPropertyName("url")]
    public string Url { get; set; }

    /// <summary>
    /// 文件 ID
    /// </summary>
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    /// <summary>
    /// 文件大小(字节)
    /// </summary>
    [JsonIgnore]
    public long? FileSize { get; set; }

    [JsonPropertyName("file_size")]
    public string? FileSizeStr
    {
        get => FileSize?.ToString();
        set => FileSize = value != null ? long.Parse(value) : default;
    }

    public override TypedMessage Clone()
    {
        return new FileData { File = File, Url = Url, FileId = FileId, FileSize = FileSize };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (FileData)obj;
        return File == other.File && Url == other.Url && FileId == other.FileId && FileSize == other.FileSize;
    }
    public override string ToString()
    {
        return $"file {File}";
    }
}

/// <summary>
/// JSON消息数据
/// 用于发送JSON格式的卡片消息
/// </summary>
public class JsonData : TypedMessage
{
    /// <summary>
    /// JSON 数据
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    public override TypedMessage Clone()
    {
        return new JsonData { Data = Data };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (JsonData)obj;
        return object.Equals(Data, other.Data);
    }
    public override string ToString()
    {
        return $"json {Data}";
    }
}

/// <summary>
/// 音乐分享数据
/// 用于分享音乐，仅支持发送，接收时会转换为 json 类型
/// </summary>
public class MusicData : TypedMessage
{
    /// <summary>
    /// 音乐平台(qq、163、kugou、kuwo、migu、custom)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; }

    /// <summary>
    /// 音乐 ID(平台非 custom 时必填)
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// 音乐链接(custom 时必填)
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 封面图片(custom 时必填)
    /// </summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>
    /// 歌手
    /// </summary>
    [JsonPropertyName("singer")]
    public string? Singer { get; set; }

    /// <summary>
    /// 标题
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// 内容描述
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    public override TypedMessage Clone()
    {
        return new MusicData { Type = Type, Id = Id, Url = Url, Image = Image, Singer = Singer, Title = Title, Content = Content };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (MusicData)obj;
        return Type == other.Type && Id == other.Id && Url == other.Url && Image == other.Image && Singer == other.Singer && Title == other.Title && Content == other.Content;
    }
    public override string ToString()
    {
        return $"music {Type} {Id}";
    }
}

/// <summary>
/// 转发消息数据
/// 用于发送合并转发消息
/// </summary>
public class ForwardData : TypedMessage
{
    /// <summary>
    /// 转发消息ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// 转发的消息内容列表(仅当解析转发内容时)
    /// </summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    public override TypedMessage Clone()
    {
        return new ForwardData { Id = Id, Content = Content };
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || GetType() != obj.GetType())
        {
            return false;
        }
        if (ReferenceEquals(this, obj))
        {
            return true;
        }
        var other = (ForwardData)obj;
        return Id == other.Id && object.Equals(Content, other.Content);
    }
    public override string ToString()
    {
        return $"forward {Id}";
    }
}
