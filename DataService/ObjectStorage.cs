using System;
using System.Threading.Tasks;

namespace DataService;

public interface IObjectStorage : IDisposable
{
    Task<string> StoreAsync(string bucket, string key, byte[] data);
    Task<byte[]?> GetAsync(string bucket, string key);
    Task<bool> ExistsAsync(string bucket, string key);
    Task<bool> DeleteAsync(string bucket, string key);
    Task<long> GetSizeAsync(string bucket, string key);
    string GetPath(string bucket, string key);
}
