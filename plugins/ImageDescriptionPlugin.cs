using System.Runtime.CompilerServices;
using DataService;
using OpenAiClient;

namespace BotPlugin;

/// <summary>
/// 图片描述统一服务：byte[] 引用缓存（零哈希）+ ImageEntry.Description 持久化缓存 + 调 vision 模型 + 写回。
/// 生产者（recorder）和消费者（AppendImageData）都通过 GetOrComputeDescriptionAsync 一个入口访问。
/// </summary>
[PluginTag("image-description", "ImageDescription", "图片描述统一服务（引用缓存+解析+入库）", priority: 998, type: PluginType.Background)]
public class ImageDescriptionPlugin : Plugin
{
    // byte[] 引用缓存：key 是 byte[] 引用（不需要哈希），自动 GC 回收
    // ConditionalWeakTable 线程安全，GetValue 实现"已有则返回，没有则创建"
    private readonly ConditionalWeakTable<byte[], Task<string?>> _byteCache = new();

    private readonly ImageInterpreterPool? _pool;
    private readonly StorageManagerPlugin _storageManager;
    private readonly bool _enabled;
    private readonly ImageInterpreterType _type;
    private readonly SemaphoreSlim _semaphore;

    public ImageDescriptionPlugin(
        PluginInterop interop,
        StorageManagerPlugin storageManager,
        LlmService llmService) : base(interop)
    {
        _storageManager = storageManager;
        _pool = llmService.ImageInterpreterPool;

        _enabled = interop.GetStructVariableOrSetDefault("enable-image-description", true);
        var typeStr = interop.GetVariableOrSetDefault("image-description-type", "Normal");
        _type = string.Equals(typeStr, "Quick", StringComparison.OrdinalIgnoreCase)
            ? ImageInterpreterType.Quick
            : ImageInterpreterType.Normal;
        var concurrency = interop.GetIntVariableOrSetDefault("image-description-concurrency", 5);
        _semaphore = new SemaphoreSlim(concurrency, concurrency);

        // timeout 配置暂存供将来给 Interpret 加 CancellationToken
        _ = interop.GetIntVariableOrSetDefault("image-description-timeout-seconds", 60);

        if (!_enabled)
        {
            Logger.Info("图片描述已禁用（enable-image-description = false）");
        }
        else if (_pool == null)
        {
            Logger.Warn("图片描述池未初始化（缺 vision 模型 token），将无法解析");
        }
        else
        {
            Logger.Info($"图片描述服务已启动，类型: {_type}, 并发: {concurrency}");
        }
    }

    /// <summary>
    /// 图片描述的统一入口。内部顺序：
    /// (1) byte[] 引用缓存：相同 byte[] 实例直接复用 task（零哈希开销）
    /// (2) ImageEntry.Description 持久化缓存：跨重启的 hash-level 缓存
    /// (3) 调 vision 模型，生成描述，写回 ImageEntry.Description
    /// 返回 null 表示禁用 / pool 不可用 / 解析失败。
    /// </summary>
    public async Task<string?> GetOrComputeDescriptionAsync(
        string hash, byte[] imageBytes, string contentType)
    {
        if (!_enabled || _pool == null || imageBytes == null || imageBytes.Length == 0)
            return null;

        // (1) byte[] 引用缓存（最快路径：相同 byte[] 实例直接复用）
        if (_byteCache.TryGetValue(imageBytes, out var cached))
        {
            return await cached;
        }

        // (2) ImageEntry.Description 持久化缓存（跨进程/重启）
        var entry = await _storageManager.GroupHistoryRecorder.GetImageByHashAsync(hash);
        if (entry != null && !string.IsNullOrEmpty(entry.Description))
        {
            return entry.Description;
        }

        // (3) 调模型 + 写回 ImageEntry
        // GetValue 回调线程安全：相同 byte[] 引用只触发一次 ComputeAndCacheAsync
        var task = _byteCache.GetValue(imageBytes, _ =>
            ComputeAndCacheAsync(hash, imageBytes, contentType));
        return await task;
    }

    private async Task<string?> ComputeAndCacheAsync(
        string hash, byte[] imageBytes, string contentType)
    {
        await _semaphore.WaitAsync();
        try
        {
            var description = await _pool!.Interpret(imageBytes, contentType, _type);
            if (!string.IsNullOrEmpty(description))
            {
                await _storageManager.GroupHistoryRecorder
                    .SetImageEntryDescriptionAsync(hash, description);
            }
            return description;
        }
        catch (Exception ex)
        {
            Logger.Warn($"图片描述解析失败 hash={hash}: {ex.Message}");
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
        _semaphore.Dispose();
        base.Dispose();
    }
}
