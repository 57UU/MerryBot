namespace BotPlugin;

public sealed partial class LlmProviderPlugin
{
    private const string DefaultModelMetaId = "default-model";
    private const string SchemaVersionMetaId = "schema-version";
    private const string SchemaVersion = "2";

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
            var all = await models.FindAllAsync();
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
        else if (schema.Value != SchemaVersion)
        {
            throw new InvalidOperationException($"llm-provider 数据库版本不受支持: {schema.Value}");
        }
    }
}
