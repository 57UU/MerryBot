window.configApi = {
    request: async function (method, url, body) {
        const response = await fetch(url, {
            method: method,
            headers: body == null ? {} : { "Content-Type": "application/json" },
            body: body == null ? undefined : JSON.stringify(body)
        });
        if (!response.ok) {
            const message = await response.text();
            throw new Error(message || `${response.status} ${response.statusText}`);
        }
        if (response.status === 204) {
            return null;
        }
        return await response.json();
    }
};
