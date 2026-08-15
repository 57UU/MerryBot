using LiteDB.Async;
using System.Text;

namespace DataProvider;

/// <summary>
/// A database view limited to one plugin's collection namespace.
/// The owning <see cref="PluginStorageDatabase"/> manages the underlying connection.
/// </summary>
public sealed class PluginDatabaseScope
{
    private readonly LiteDatabaseAsync _database;

    internal PluginDatabaseScope(LiteDatabaseAsync database, string pluginId)
    {
        _database = database;
        PluginId = pluginId;
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
        return $"plugin_{encodedPluginId.Length}_{encodedPluginId}_{encodedName}";
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
