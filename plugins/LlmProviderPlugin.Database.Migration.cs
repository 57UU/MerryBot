namespace BotPlugin;

public sealed partial class LlmProviderPlugin
{
    private const string DefaultModelMetaId = "default-model";
    private const string SchemaVersionMetaId = "schema-version";
    private const string SchemaVersion = "3";

    private async Task EnsureIndexesAsync()
    {
        await providers.EnsureIndexAsync(item => item.Id);
        await models.EnsureIndexAsync(item => item.ProviderId);
        await keys.EnsureIndexAsync(item => item.ProviderId);
        var schema = await meta.FindByIdAsync(SchemaVersionMetaId);
        if (schema == null)
        {
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionMetaId, Value = SchemaVersion });
        }
        else if (schema.Value == "1")
        {
            // 1 -> 2: 补 ReasoningOptions 空数组，LiteDB 缺字段读作 null，需显式回填
            // FindAllAsync 返回延迟枚举：先物化再逐条写，避免枚举中写造成同一异步流锁重入
            var all = (await models.FindAllAsync()).ToList();
            foreach (var m in all)
            {
                if (m.ReasoningOptions == null)
                {
                    m.ReasoningOptions = [];
                    await models.UpsertAsync(m);
                }
            }
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionMetaId, Value = SchemaVersion });
        }
        else if (schema.Value == "2")
        {
            // 2 -> 3: 历史上目录导入曾把 %2F 字面存进模型 ID，与解码后的引用对不上
            //（点“设为默认”时查不到模型 → KeyNotFoundException → 500）。统一重命名为解码形，
            // 并修正默认模型指针；两种形态并存时保留更新的那条。
            var allModels = (await models.FindAllAsync()).ToList();
            var renamed = 0;
            foreach (var m in allModels)
            {
                if (!m.Id.Contains('%')) continue;
                var oldId = m.Id;
                var canonical = NormalizeModelId(oldId, nameof(oldId));
                if (string.Equals(canonical, oldId, StringComparison.Ordinal)) continue;
                var existing = await models.FindByIdAsync(canonical);
                if (existing == null)
                {
                    m.Id = canonical;
                    await models.InsertAsync(m);
                }
                else
                {
                    var winner = existing.UpdatedAtUtc >= m.UpdatedAtUtc ? existing : m;
                    existing.ProviderId = winner.ProviderId;
                    existing.RemoteModelId = winner.RemoteModelId;
                    existing.ContextLength = winner.ContextLength;
                    existing.MaxOutputTokens = winner.MaxOutputTokens;
                    existing.Capabilities = winner.Capabilities;
                    existing.ReasoningEffort = winner.ReasoningEffort;
                    existing.ReasoningOptions = winner.ReasoningOptions;
                    existing.EnablePromptCache = winner.EnablePromptCache;
                    existing.Enabled = winner.Enabled;
                    existing.CatalogProviderId = winner.CatalogProviderId;
                    existing.CatalogModelId = winner.CatalogModelId;
                    existing.CatalogUpdatedAtUtc = winner.CatalogUpdatedAtUtc;
                    existing.UpdatedAtUtc = winner.UpdatedAtUtc;
                    await models.UpsertAsync(existing);
                }
                await models.DeleteAsync(oldId);
                var pointer = await meta.FindByIdAsync(DefaultModelMetaId);
                if (pointer != null && string.Equals(pointer.Value, oldId, StringComparison.OrdinalIgnoreCase))
                {
                    pointer.Value = canonical;
                    await meta.UpsertAsync(pointer);
                }
                renamed++;
            }
            if (renamed > 0)
            {
                Logger.Info($"llm-provider 迁移 2->3：规范化 {renamed} 个含百分号编码的模型 ID。");
            }
            await meta.UpsertAsync(new MetaRecord { Id = SchemaVersionMetaId, Value = SchemaVersion });
        }
        else if (schema.Value != SchemaVersion)
        {
            throw new InvalidOperationException($"llm-provider 数据库版本不受支持: {schema.Value}");
        }
    }
}
