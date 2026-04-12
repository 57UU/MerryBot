function extractSearchResults(htmlBody) {
    const results = [];

    // 创建临时DOM元素来解析HTML
    const tempDiv = document.createElement('div');
    tempDiv.innerHTML = htmlBody;

    // 查找所有的搜索结果项
    const resultItems = tempDiv.querySelectorAll('li.b_algo');

    resultItems.forEach(item => {
        // 提取标题
        const titleElement = item.querySelector('h2 a');
        const title = titleElement ? titleElement.textContent.trim() : '';

        // 提取链接
        const link = titleElement ? titleElement.getAttribute('href') : '';

        // 提取内容
        const contentElement = item.querySelector('.b_caption p');
        const content = contentElement ? contentElement.textContent.trim() : '';

        if (title && link) {
            results.push({
                title: title,
                content: content,
                link: link
            });
        }
    });

    return results;
}

const htmlBody = document.body.innerHTML; 
const searchResults = extractSearchResults(htmlBody);
return JSON.stringify(searchResults)