window.skillApi = {
    upload: async function (inputId, url) {
        const input = document.getElementById(inputId);
        const file = input?.files?.[0];
        if (!file) {
            throw new Error("请选择要上传的 Skill 文件。");
        }
        const form = new FormData();
        form.append("file", file, file.name);
        const response = await fetch(url, { method: "POST", body: form });
        if (!response.ok) {
            throw new Error(await skillApi.readError(response));
        }
        input.value = "";
    },
    // 与 configApi/llmProviderApi 同约定：优先取 { error } 原因短语，非 JSON 原样上抛由调用方压缩展示
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
    }
};
