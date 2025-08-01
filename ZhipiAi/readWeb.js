/**
 * 获取页面中所有a标签的href属性
 * @returns {Array<string>} - 包含所有href属性值的数组
 */
function extractAllHrefs() {
  // 创建一个空数组存储href值
  const hrefs = [];

  // 获取页面中所有的a标签
  const aTags = document.querySelectorAll('a');

  // 遍历所有a标签，提取href属性
  aTags.forEach(aTag => {
    const href = aTag.getAttribute('href');
    if (href) {
      // 确保href不为空
      hrefs.push(href);
    }
  });

  return hrefs;
}

/**
 * 获取净化后的HTML，只保留可见文本和a标签的href属性
 * @returns {string} - 净化后的HTML
 */
function getCleanHTML() {
  // 克隆一份文档，避免修改原页面
  const clonedDoc = document.documentElement.cloneNode(true);

  // 移除所有不可见元素和不需要的标签
  removeInvisibleElements(clonedDoc);

  // 清理所有标签的属性，只保留a标签的href属性
  cleanAttributes(clonedDoc);

  // 返回净化后的HTML
  return clonedDoc.outerHTML;
}

/**
 * 移除不可见元素和不需要的标签
 * @param {HTMLElement} doc - 文档元素
 */
function removeInvisibleElements(doc) {
  // 移除所有CSS相关元素
  doc.querySelectorAll('style, link[rel="stylesheet"]').forEach(el => el.remove());

  // 移除脚本和其他不可见元素
  const REMOVABLE_TAGS = [
    'script', 'noscript', 'template', 'svg', 'canvas',
    'audio', 'video', 'iframe', 'embed', 'object', 'head'
  ];

  REMOVABLE_TAGS.forEach(tag => {
    doc.querySelectorAll(tag).forEach(el => el.remove());
  });

  // 移除display为none的元素
  doc.querySelectorAll('[style*="display:none"], [hidden]').forEach(el => el.remove());

  // 移除嵌套的div
  removeNestedDivs(doc);

  // 移除空元素
  removeEmptyElements(doc);
}

/**
 * 移除嵌套的div元素
 * @param {HTMLElement} doc - 文档元素
 */
function removeNestedDivs(doc) {
  // 获取所有div元素
  const divs = doc.querySelectorAll('div');

  divs.forEach(div => {
    // 检查是否只有一个子元素，且该子元素也是div
    while (div.childNodes.length === 1 && div.firstChild.tagName && div.firstChild.tagName.toLowerCase() === 'div') {
      const childDiv = div.firstChild;
      // 将子div的所有子节点移动到当前div
      while (childDiv.firstChild) {
        div.insertBefore(childDiv.firstChild, childDiv);
      }
      // 移除子div
      div.removeChild(childDiv);
    }
  });
}

/**
 * 移除空元素
 * @param {HTMLElement} doc - 文档元素
 */
function removeEmptyElements(doc) {
  // 获取所有元素
  const allElements = doc.querySelectorAll('*');
  
  allElements.forEach(el => {
    // 检查元素是否为空（没有子节点或只有空白节点）
    const isEmpty = el.childNodes.length === 0 || 
                    (el.childNodes.length === 1 && el.firstChild.nodeType === 3 && el.firstChild.textContent.trim() === '');
    
    if (isEmpty) {
      el.remove();
    }
  });
}

/**
 * 清理所有标签的属性，只保留a标签的href属性
 * @param {HTMLElement} doc - 文档元素
 */
function cleanAttributes(doc) {
  // 获取所有元素
  const allElements = doc.querySelectorAll('*');

  allElements.forEach(el => {
    // 保存a标签的href属性
    let href = null;
    if (el.tagName.toLowerCase() === 'a') {
      href = el.getAttribute('href');
    }

    // 移除所有属性
    [...el.attributes].forEach(attr => el.removeAttribute(attr.name));

    // 为a标签恢复href属性
    if (el.tagName.toLowerCase() === 'a' && href) {
      el.setAttribute('href', href);
    }
  });
}

// 返回净化后的HTML
return getCleanHTML();

// 如果需要仅获取href数组，可以使用以下行
// return extractAllHrefs();