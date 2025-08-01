/**
 * 获取当前页面去除不可见内容后的纯 HTML
 * @param {Object} [options]
 * @param {boolean} [options.stripAttrs] - 是否去掉所有属性（默认 false）
 * @returns {string} - 净化后的 HTML
 */
function getCleanHTML(options = {}) {
  const { stripAttrs = false } = options;

  // 1. 克隆一份文档，避免修改原页面
  const clonedDoc = document.documentElement.cloneNode(true);

  // 2. 需要删除的标签列表
  const REMOVABLE_TAGS = [
    'style',
    'link[rel="stylesheet"]',
    'script',
    'noscript',
    'template',
    'svg',
    'canvas',
    'audio',
    'video',
    'iframe',
    'embed',
    'object',
    'head', // 整个 <head> 也可以不要
  ];

  REMOVABLE_TAGS.forEach(selector => {
    clonedDoc.querySelectorAll(selector).forEach(el => el.remove());
  });

  // 3. 去掉 style 内联属性
  clonedDoc.querySelectorAll('*').forEach(el => {
    el.removeAttribute('style');
    if (stripAttrs) {
      // 去掉所有属性
      [...el.attributes].forEach(attr => el.removeAttribute(attr.name));
    } else {
      // 仅去掉无助于纯文本展示的属性
      ['class', 'id', 'data-*'].forEach(attr => {
        if (attr === 'data-*') {
          [...el.attributes]
            .filter(a => a.name.startsWith('data-'))
            .forEach(a => el.removeAttribute(a.name));
        } else {
          el.removeAttribute(attr);
        }
      });
    }
  });

  // 4. 返回字符串
  return clonedDoc.outerHTML;
}
return getCleanHTML()