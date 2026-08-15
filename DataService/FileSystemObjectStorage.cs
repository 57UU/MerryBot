using System.Collections.Concurrent;

namespace DataService;

public class FileSystemObjectStorage : IObjectStorage
{
    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, string> _bucketPathCache = new();
    private bool _disposed;

    public FileSystemObjectStorage(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    /// <summary>
    /// 校验 bucket 名称：拒绝空值、绝对路径与路径穿越（..）。
    /// </summary>
    private static void ValidateBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException("bucket 不能为空", nameof(bucket));
        }
        if (Path.IsPathRooted(bucket) || bucket.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"非法的 bucket 名称: {bucket}", nameof(bucket));
        }
    }

    /// <summary>
    /// 校验 bucket 与 key：拒绝空值、绝对路径与路径穿越（..）。
    /// </summary>
    private static void ValidateKey(string bucket, string key)
    {
        ValidateBucket(bucket);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("key 不能为空", nameof(key));
        }
        if (Path.IsPathRooted(key) || key.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"非法的 key: {key}", nameof(key));
        }
    }

    private string GetBucketPath(string bucket)
    {
        ValidateBucket(bucket);
        // 缓存 bucket 目录路径，避免高频读路径重复 Directory.Exists/CreateDirectory
        return _bucketPathCache.GetOrAdd(bucket, b =>
        {
            var bucketPath = Path.Combine(_basePath, b);
            Directory.CreateDirectory(bucketPath);
            return bucketPath;
        });
    }

    public string GetPath(string bucket, string key)
    {
        ValidateKey(bucket, key);
        return Path.Combine(GetBucketPath(bucket), key);
    }

    public async Task<string> StoreAsync(string bucket, string key, byte[] data)
    {
        var filePath = GetPath(bucket, key);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        // 先写同目录临时文件再原子替换，避免读取方看到半写状态；失败时清理临时文件
        var tempPath = filePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, data);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 忽略临时文件清理失败
            }
            throw;
        }
        return filePath;
    }

    public async Task<byte[]?> GetAsync(string bucket, string key)
    {
        var filePath = GetPath(bucket, key);
        if (!File.Exists(filePath))
        {
            return null;
        }
        return await File.ReadAllBytesAsync(filePath);
    }

    public Task<bool> ExistsAsync(string bucket, string key)
    {
        var filePath = GetPath(bucket, key);
        return Task.FromResult(File.Exists(filePath));
    }

    public Task<bool> DeleteAsync(string bucket, string key)
    {
        var filePath = GetPath(bucket, key);
        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }
        File.Delete(filePath);
        return Task.FromResult(true);
    }

    public Task<long> GetSizeAsync(string bucket, string key)
    {
        var filePath = GetPath(bucket, key);
        if (!File.Exists(filePath))
        {
            return Task.FromResult(-1L);
        }
        var fileInfo = new FileInfo(filePath);
        return Task.FromResult(fileInfo.Length);
    }

    public async Task<long> GetTotalSizeAsync(string bucket)
    {
        return await Task.Run(() =>
        {
            var bucketPath = GetBucketPath(bucket);
            if (!Directory.Exists(bucketPath))
            {
                return 0L;
            }
            var files = Directory.GetFiles(bucketPath, "*", SearchOption.AllDirectories);
            long totalSize = 0;
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                totalSize += fileInfo.Length;
            }
            return totalSize;
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
