window.scrollToElementBottom = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
};

window.handleScrollForLoadMore = function (elementId, dotNetRef) {
    clearTimeout(window._scrollTimer);
    window._scrollTimer = setTimeout(() => {
        const el = document.getElementById(elementId);
        if (el && el.scrollTop <= 50) {
            dotNetRef.invokeMethodAsync('LoadMoreMessages');
        }
    }, 200);
};

window.getScrollInfo = function (elementId) {
    const el = document.getElementById(elementId);
    if (!el) return { scrollHeight: 0, scrollTop: 0 };
    return { scrollHeight: el.scrollHeight, scrollTop: el.scrollTop };
};

window.setScrollTop = function (elementId, value) {
    const el = document.getElementById(elementId);
    if (el) el.scrollTop = value;
};
