// 纯 DOM 助手：页面滚动定位（业务 API 已全部迁为 Blazor 直连，不再经 fetch 回调 HTTP）。
window.configApi = {
    scrollTo: function (elementId) {
        document.getElementById(elementId)?.scrollIntoView({ behavior: "smooth", block: "start" });
    },
    scrollToBottom: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollTop = el.scrollHeight;
    }
};
