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
            throw new Error((await response.text()) || `${response.status} ${response.statusText}`);
        }
        input.value = "";
    }
};
