using System;
using System.IO;
using System.Threading.Tasks;

namespace DataService;

public class FileSystemObjectStorage : IObjectStorage
{
    private readonly string _basePath;
    private bool _disposed;

    public FileSystemObjectStorage(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    private string GetBucketPath(string bucket)
    {
        var bucketPath = Path.Combine(_basePath, bucket);
        if (!Directory.Exists(bucketPath))
        {
            Directory.CreateDirectory(bucketPath);
        }
        return bucketPath;
    }

    public string GetPath(string bucket, string key)
    {
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
        await File.WriteAllBytesAsync(filePath, data);
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
