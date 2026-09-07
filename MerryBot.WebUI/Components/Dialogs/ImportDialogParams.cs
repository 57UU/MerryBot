namespace MerryBot.WebUI.Components.Dialogs;

/// <summary>目录导入对话框参数：导入成功后置位 <see cref="Changed"/>；
/// 「填入编辑表单」把目录模型带回 <see cref="FillIntoForm"/> 后关闭，由页面接着弹模型框。</summary>
public sealed class ImportDialogParams
{
    public bool Changed { get; set; }

    public LlmCatalogModelDto? FillIntoForm { get; set; }
}
