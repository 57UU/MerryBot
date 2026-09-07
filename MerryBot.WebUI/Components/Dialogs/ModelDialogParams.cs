namespace MerryBot.WebUI.Components.Dialogs;

/// <summary>模型对话框参数：<see cref="Existing"/> 非空表示编辑；<see cref="Prefill"/> 为目录导入接力带入的预填；
/// 成功保存后回填 <see cref="SavedModelId"/>。</summary>
public sealed class ModelDialogParams
{
    public required IReadOnlyList<LlmProviderDto> Providers { get; init; }

    public LlmModelDto? Existing { get; init; }

    public LlmCatalogModelDto? Prefill { get; init; }

    public string? SavedModelId { get; set; }
}
