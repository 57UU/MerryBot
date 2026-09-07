namespace MerryBot.WebUI.Components.Dialogs;

/// <summary>Provider 对话框参数：<see cref="Existing"/> 为 null 表示新增；对话框成功保存后回填 <see cref="SavedProviderId"/>。</summary>
public sealed class ProviderDialogParams
{
    public LlmProviderDto? Existing { get; init; }

    public string? SavedProviderId { get; set; }

    public string? SavedProviderName { get; set; }
}
