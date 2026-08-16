using LiteDB.Async;

namespace DataProvider;

/// <summary>
/// <see cref="PluginStorageDatabase"/> 的 schema 迁移：基于 LiteDB UserVersion 的版本号
/// 依次执行未完成的迁移步骤，每完成一步立即持久化版本号，失败时不会重复执行已完成步骤。
/// 幂等：已是最新版本时直接返回。
/// </summary>
public partial class PluginStorageDatabase
{
    /// <summary>
    /// 数据库 schema 版本。每次新增迁移步骤时递增。
    /// - 0: 初始版本（键无前缀，如 Plugin_Data_Table 直接以插件 Id 为键）
    /// - 1: 键加前缀：Plugin_Data_Table / Plugin_Config_Table 的无前缀键迁移为 "plugin/" 前缀，
    ///      与新代码（StorePluginData/GetPluginData 的 prefix 参数）保持一致
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>单个迁移步骤：从 <paramref name="FromVersion"/> 迁移到 FromVersion+1。</summary>
    private record DbMigration(int FromVersion, string Name, Func<PluginStorageDatabase, Task> Action);

    /// <summary>有序迁移步骤表。只追加新条目，不修改已有条目。</summary>
    private static readonly DbMigration[] Migrations =
    {
        new(0, "plugin data/config keys: bare → plugin/ prefix",
            static self => self.MigratePrefixV1Async()),
    };

    /// <summary>
    /// 执行数据库迁移。根据 LiteDB 的 UserVersion 字段判断当前版本，
    /// 依次执行未完成的迁移步骤，每完成一步立即写入新版本号。
    /// </summary>
    public async Task MigrateAsync()
    {
        int current = _db.UserVersion;
        for (int i = current; i < CurrentSchemaVersion; i++)
        {
            var step = Migrations[i];
            await step.Action(this);
            // 每完成一步立即持久化版本号，避免中途失败重复执行已完成的步骤
            _db.UserVersion = i + 1;
        }
    }

    /// <summary>
    /// 迁移 v0 → v1：把 <see cref="_pluginDataCollection"/> 与 <see cref="_groupConfigCollection"/>
    /// 中的无前缀键统一迁移为 "plugin/" 前缀。
    /// 此前 GetPluginData/GetPluginConfig 依赖无前缀旧键的读取回退，迁移完成后旧键消失，
    /// 回退逻辑不再命中（保留作防御，避免其他路径写入的裸键读不到）。
    /// </summary>
    private async Task MigratePrefixV1Async()
    {
        await MigrateCollectionPrefixAsync(_pluginDataCollection, "plugin");
        await MigrateCollectionPrefixAsync(_groupConfigCollection, "plugin");
    }

    /// <summary>把集合中所有不含 "/" 的键改为 "{prefix}/{原键}"；目标键已存在时以新数据为准，丢弃旧键。</summary>
    private static async Task MigrateCollectionPrefixAsync(ILiteCollectionAsync<PluginData> collection, string prefix)
    {
        var items = await collection.FindAllAsync();
        foreach (var item in items)
        {
            if (item.Id.Contains('/'))
            {
                continue;
            }
            await collection.UpsertAsync(new PluginData { Id = $"{prefix}/{item.Id}", Value = item.Value });
            await collection.DeleteAsync(item.Id);
        }
    }
}
