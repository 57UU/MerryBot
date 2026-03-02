using LiteDB;
using LiteDB.Async;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DataService;

public static class StorageMigration
{
    public static async Task<MigrationResult> MigrateAsync(
        string sourceDbPath,
        string targetDbPath,
        string storagePath,
        int batchSize = 100)
    {
        var result = new MigrationResult();
        
        using var sourceDb = new LiteDatabaseAsync(sourceDbPath);
        using var targetDb = new LiteDatabaseAsync(targetDbPath);
        var objectStorage = new FileSystemObjectStorage(storagePath);
        
        var sourceImages = sourceDb.GetCollection<OldImageEntry>("images");
        var sourceFiles = sourceDb.GetCollection<OldFileEntry>("files");
        
        var targetImages = targetDb.GetCollection<ImageEntry>("images");
        var targetFiles = targetDb.GetCollection<FileEntry>("files");
        
        await targetImages.EnsureIndexAsync(x => x.Hash);
        await targetFiles.EnsureIndexAsync(x => x.Hash);
        
        await MigrateImagesAsync(sourceImages, targetImages, objectStorage, result, batchSize);
        await MigrateFilesAsync(sourceFiles, targetFiles, objectStorage, result, batchSize);
        
        return result;
    }

    private static async Task MigrateImagesAsync(
        ILiteCollectionAsync<OldImageEntry> source,
        ILiteCollectionAsync<ImageEntry> target,
        IObjectStorage storage,
        MigrationResult result,
        int batchSize)
    {
        var total = await source.CountAsync();
        var processed = 0;
        
        while (processed < total)
        {
            var batch = await source.Query()
                .Skip(processed)
                .Limit(batchSize)
                .ToListAsync();
            
            foreach (var oldEntry in batch)
            {
                try
                {
                    if (oldEntry.Data != null && oldEntry.Data.Length > 0)
                    {
                        await storage.StoreAsync("images", oldEntry.Hash, oldEntry.Data);
                    }
                    
                    var newEntry = new ImageEntry(oldEntry.Id, oldEntry.OriginalUrl, oldEntry.Hash);
                    await target.InsertAsync(newEntry);
                    result.ImagesMigrated++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Image {oldEntry.Id}: {ex.Message}");
                }
            }
            
            processed += batch.Count;
            result.ImagesProcessed = processed;
        }
    }

    private static async Task MigrateFilesAsync(
        ILiteCollectionAsync<OldFileEntry> source,
        ILiteCollectionAsync<FileEntry> target,
        IObjectStorage storage,
        MigrationResult result,
        int batchSize)
    {
        var total = await source.CountAsync();
        var processed = 0;
        
        while (processed < total)
        {
            var batch = await source.Query()
                .Skip(processed)
                .Limit(batchSize)
                .ToListAsync();
            
            foreach (var oldEntry in batch)
            {
                try
                {
                    if (oldEntry.Data != null && oldEntry.Data.Length > 0)
                    {
                        await storage.StoreAsync("files", oldEntry.Hash, oldEntry.Data);
                    }
                    
                    var newEntry = new FileEntry(oldEntry.Id, oldEntry.OriginalUrl, oldEntry.Hash);
                    await target.InsertAsync(newEntry);
                    result.FilesMigrated++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"File {oldEntry.Id}: {ex.Message}");
                }
            }
            
            processed += batch.Count;
            result.FilesProcessed = processed;
        }
    }

    public static async Task<MigrationResult> MigrateInPlaceAsync(
        string dbPath,
        string storagePath,
        int batchSize = 100)
    {
        var result = new MigrationResult();
        var objectStorage = new FileSystemObjectStorage(storagePath);
        
        using var db = new LiteDatabaseAsync(dbPath);
        var imagesCollection = db.GetCollection<OldImageEntry>("images");
        var filesCollection = db.GetCollection<OldFileEntry>("files");
        
        await MigrateImagesInPlaceAsync(imagesCollection, objectStorage, result, batchSize);
        await MigrateFilesInPlaceAsync(filesCollection, objectStorage, result, batchSize);
        
        return result;
    }

    private static async Task MigrateImagesInPlaceAsync(
        ILiteCollectionAsync<OldImageEntry> collection,
        IObjectStorage storage,
        MigrationResult result,
        int batchSize)
    {
        var total = await collection.CountAsync();
        var processed = 0;
        
        while (processed < total)
        {
            var batch = await collection.Query()
                .Skip(processed)
                .Limit(batchSize)
                .ToListAsync();
            
            foreach (var entry in batch)
            {
                try
                {
                    if (entry.Data != null && entry.Data.Length > 0)
                    {
                        await storage.StoreAsync("images", entry.Hash, entry.Data);
                        entry.Data = null;
                        await collection.UpdateAsync(entry);
                    }
                    result.ImagesMigrated++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Image {entry.Id}: {ex.Message}");
                }
            }
            
            processed += batch.Count;
            result.ImagesProcessed = processed;
        }
    }

    private static async Task MigrateFilesInPlaceAsync(
        ILiteCollectionAsync<OldFileEntry> collection,
        IObjectStorage storage,
        MigrationResult result,
        int batchSize)
    {
        var total = await collection.CountAsync();
        var processed = 0;
        
        while (processed < total)
        {
            var batch = await collection.Query()
                .Skip(processed)
                .Limit(batchSize)
                .ToListAsync();
            
            foreach (var entry in batch)
            {
                try
                {
                    if (entry.Data != null && entry.Data.Length > 0)
                    {
                        await storage.StoreAsync("files", entry.Hash, entry.Data);
                        entry.Data = null;
                        await collection.UpdateAsync(entry);
                    }
                    result.FilesMigrated++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"File {entry.Id}: {ex.Message}");
                }
            }
            
            processed += batch.Count;
            result.FilesProcessed = processed;
        }
    }
}

public class MigrationResult
{
    public int ImagesProcessed { get; set; }
    public int ImagesMigrated { get; set; }
    public int FilesProcessed { get; set; }
    public int FilesMigrated { get; set; }
    public List<string> Errors { get; set; } = new();
    
    public bool Success => Errors.Count == 0;
    
    public override string ToString()
    {
        return $"Images: {ImagesMigrated}/{ImagesProcessed}, Files: {FilesMigrated}/{FilesProcessed}, Errors: {Errors.Count}";
    }
}

public class OldImageEntry
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public byte[]? Data { get; set; }
}

public class OldFileEntry
{
    public long Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public byte[]? Data { get; set; }
}
