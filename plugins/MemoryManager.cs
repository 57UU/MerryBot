namespace BotPlugin;

/// <summary>
/// 基于文件的记忆存储，每个群一个目录，每条记忆一个 .md 文件
/// </summary>
public class MemoryManager
{
    private readonly string _basePath;

    public MemoryManager(string basePath)
    {
        _basePath = Path.Combine(basePath, "memory");
    }

    private string GetGroupDir(long groupId)
    {
        return Path.Combine(_basePath, groupId.ToString());
    }

    /// <summary>
    /// 保存或更新一条记忆（不允许写入 index）
    /// </summary>
    public async Task SaveAsync(long groupId, string key, string content)
    {
        if (key == "index") throw new ArgumentException("index 是保留名称，不可写入");
        var dir = GetGroupDir(groupId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{key}.md");
        await File.WriteAllTextAsync(filePath, content);
    }

    /// <summary>
    /// 获取所有记忆的 key 列表
    /// </summary>
    public string[] ListKeys(long groupId)
    {
        var dir = GetGroupDir(groupId);
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(k => k != "index")
            .ToArray();
    }

    /// <summary>
    /// 读取一条记忆的内容
    /// </summary>
    /// <returns>记忆内容，不存在返回 null</returns>
    public async Task<string?> ReadAsync(long groupId, string key)
    {
        var filePath = Path.Combine(GetGroupDir(groupId), $"{key}.md");
        if (!File.Exists(filePath)) return null;
        return await File.ReadAllTextAsync(filePath);
    }

    /// <summary>
    /// 删除一条记忆（不允许删除 index）
    /// </summary>
    /// <returns>是否成功删除</returns>
    public bool Delete(long groupId, string key)
    {
        if (key == "index") return false;
        var filePath = Path.Combine(GetGroupDir(groupId), $"{key}.md");
        if (!File.Exists(filePath)) return false;
        File.Delete(filePath);
        return true;
    }

    /// <summary>
    /// 读取 index.md 内容（特殊文件，始终注入 prompt，模型不可修改）
    /// 如果文件不存在，自动创建一个空的 index.md
    /// </summary>
    /// <returns>index.md 内容，不存在时返回空字符串</returns>
    public string ReadIndex(long groupId)
    {
        var filePath = Path.Combine(GetGroupDir(groupId), "index.md");
        if (!File.Exists(filePath))
        {
            Directory.CreateDirectory(GetGroupDir(groupId));
            File.WriteAllText(filePath, string.Empty);
            return string.Empty;
        }
        return File.ReadAllText(filePath);
    }

    /// <summary>
    /// 生成用于 DynamicPromptFunc 的 prompt 注入文本
    /// 包含 index.md 内容（如有）+ 记忆 key 列表
    /// </summary>
    /// <returns>格式化的注入文本，无任何内容返回 null</returns>
    public string? GetPromptInjection(long groupId)
    {
        var indexContent = ReadIndex(groupId);
        var keys = ListKeys(groupId);

        if (string.IsNullOrEmpty(indexContent) && keys.Length == 0) return null;

        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(indexContent))
        {
            sb.AppendLine(indexContent);
            sb.AppendLine();
        }

        if (keys.Length > 0)
        {
            sb.AppendLine("## 已记忆的信息");
            foreach (var k in keys)
            {
                sb.AppendLine($"- {k}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
