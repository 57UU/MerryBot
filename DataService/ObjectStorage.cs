namespace DataService;

public interface IObjectStorage : IDisposable
{
    Task<string> StoreAsync(string bucket, string key, byte[] data);
    Task<byte[]?> GetAsync(string bucket, string key);
    Task<bool> ExistsAsync(string bucket, string key);
    Task<bool> DeleteAsync(string bucket, string key);
    Task<long> GetSizeAsync(string bucket, string key);
    Task<long> GetTotalSizeAsync(string bucket);
    string GetPath(string bucket, string key);
}
