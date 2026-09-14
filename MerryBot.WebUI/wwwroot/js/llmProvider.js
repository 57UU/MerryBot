// 纯 DOM 助手：模型对话框的下拉对齐与滚动（业务 API 已全部迁为 Blazor 直连，不再经 fetch 回调 HTTP）。
window.llmProviderApi = {
    scrollIntoView: function (element) {
        element.scrollIntoView({ behavior: "smooth", block: "start" });
    },
    setSelectValue: function (elementId, value) {
        var el = document.getElementById(elementId);
        if (el) {
            el.value = value;
        }
    }
};
