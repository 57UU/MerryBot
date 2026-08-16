using LiteDB.Async;
using System.Text;

namespace DataProvider;

/// <summary>
/// A database view limited to one plugin's collection namespace.
/// prefix 控制物理集合名的命名空间（如 "plugin" 或 "core"），与 <see cref="PluginStorageDatabase.CreateScope"/> 的 prefix 一致。
/// The owning <see cref="PluginStorageDatabase"/> manages the underlying connection.
/// </summary>
public sealed class PluginDatabaseScope
{
    private readonly LiteDatabaseAsync _database;
    private readonly string _prefix;

    internal PluginDatabaseScope(LiteDatabaseAsync database, string pluginId, string prefix = "plugin")
    {
        _database = database;
        PluginId = pluginId;
        _prefix = string.IsNullOrWhiteSpace(prefix) ? "plugin" : prefix.Trim();
    }

    /// <summary>
    /// The plugin identifier that owns this database scope.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Gets or creates a typed collection in this plugin's namespace.
    /// </summary>
    public ILiteCollectionAsync<T> GetCollection<T>(string name)
        => _database.GetCollection<T>(GetPhysicalCollectionName(name));

    /// <summary>
    /// Deletes a collection in this plugin's namespace.
    /// </summary>
    public Task<bool> DropCollectionAsync(string name)
        => _database.DropCollectionAsync(GetPhysicalCollectionName(name));

    /// <summary>编码后的集合名长度上限（字符数），防止异常名称导致物理集合名过长。</summary>
    private const int MaxCollectionNameLength = 100;

    private string GetPhysicalCollectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var encodedPluginId = Encode(PluginId);
        var encodedName = Encode(name);
        if (encodedName.Length > MaxCollectionNameLength)
        {
            throw new ArgumentException($"集合名过长：编码后 {encodedName.Length} 个字符，超过上限 {MaxCollectionNameLength}", nameof(name));
        }
        return $"{_prefix}_{encodedPluginId.Length}_{encodedPluginId}_{encodedName}";
    }

    /// <summary>
    /// 把任意 UTF-8 字符串编码为 LiteDB 集合名合法字符集。
    /// LiteDB 5 集合名只允许字母/数字/_/$（实测，写入时校验），拒绝 - . / + = 空格等；
    /// 故 base64 后把 + 与 / 统一映射为 _、去掉 = 填充。本编码仅用于命名隔离，不需可逆。
    /// 注意：+ 不能映射为 URL-safe 常见的 -（- 同样被 LiteDB 拒绝）。
    /// </summary>
    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '_')
            .Replace('/', '_');
}
