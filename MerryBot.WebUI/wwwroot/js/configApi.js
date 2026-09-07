window.configApi = {
    request: async function (method, url, body) {
        const response = await fetch(url, {
            method: method,
            headers: body == null ? {} : { "Content-Type": "application/json" },
            body: body == null ? undefined : JSON.stringify(body)
        });
        if (!response.ok) {
            throw new Error(await configApi.readError(response));
        }
        if (response.status === 204) {
            return null;
        }
        return await response.json();
    },
    // 错误体优先取服务端约定的 { error } 原因短语；非 JSON（整页 HTML 等）原样上抛，由调用方压缩展示
    readError: async function (response) {
        const text = await response.text();
        if (!text) {
            return `${response.status} ${response.statusText}`;
        }
        try {
            const data = JSON.parse(text);
            if (data && typeof data.error === "string" && data.error) {
                return data.error;
            }
        } catch (e) { /* 非 JSON，保持原文 */ }
        return text;
    },
    scrollTo: function (elementId) {
        document.getElementById(elementId)?.scrollIntoView({ behavior: "smooth", block: "start" });
    },
    scrollToBottom: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollTop = el.scrollHeight;
    }
};
