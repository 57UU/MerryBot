// 亮/暗主题切换：data-theme 挂在 <html> 上，选择持久化到 localStorage。
// 首屏由 App.razor 内联脚本先行设置，避免闪烁；这里只负责切换与读取。
(function () {
    "use strict";

    var KEY = "merrybot-theme";

    function current() {
        var t = document.documentElement.dataset.theme;
        return t === "dark" ? "dark" : "light";
    }

    window.theme = {
        get: function () {
            try {
                var saved = localStorage.getItem(KEY);
                if (saved === "light" || saved === "dark") return saved;
            } catch (e) { /* 隐私模式等场景下 localStorage 不可用 */ }
            return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
                ? "dark" : "light";
        },
        apply: function (t) {
            document.documentElement.dataset.theme = t === "dark" ? "dark" : "light";
        },
        toggle: function () {
            var next = current() === "dark" ? "light" : "dark";
            this.apply(next);
            try { localStorage.setItem(KEY, next); } catch (e) { /* 忽略写入失败 */ }
            return next;
        }
    };
})();
