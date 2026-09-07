namespace MerryBot.WebUI.Components.Dialogs;

/// <summary>目录模型列表对话框参数：展示指定 Provider 的模型；导入成功后置位 <see cref="Changed"/>；
/// 「填入编辑表单」把目录模型带回 <see cref="FillIntoForm"/> 后关闭，由外层接力。</summary>
public sealed class CatalogModelsDialogParams
{
    public required LlmCatalogProviderDto Provider { get; init; }

    public bool Changed { get; set; }

    public LlmCatalogModelDto? FillIntoForm { get; set; }
}
