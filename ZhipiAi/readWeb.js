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

  // 移除只有一个子元素的元素
  removeSingleChildElements(doc);

  // 移除空元素
  removeEmptyElements(doc);
}

/**
 * 移除只有一个子元素的元素，将子元素的内容提升到父元素
 * @param {HTMLElement} doc - 文档元素
 */
function removeSingleChildElements(doc) {
  // 获取所有元素
  const allElements = doc.querySelectorAll('*');
  
  // 转换为数组，避免在遍历过程中DOM修改导致的问题
  const elementsToProcess = Array.from(allElements);
  
  elementsToProcess.forEach(el => {
    // 检查是否只有一个子元素，且该子元素是元素节点
    while (el.childNodes.length === 1 && el.firstChild.nodeType === 1) {
      const childEl = el.firstChild;
      
      // 避免处理a标签，因为我们要保留其href属性
      if (childEl.tagName.toLowerCase() === 'a') {
        break;
      }
      
      // 将子元素的所有子节点移动到当前元素
      while (childEl.firstChild) {
        el.insertBefore(childEl.firstChild, childEl);
      }
      
      // 移除子元素
      el.removeChild(childEl);
    }
  });
}

/**
 * 移除空元素
 * @param {HTMLElement} doc - 文档元素
 */
function removeEmptyElements(doc) {
  // 递归检查元素是否为空
  function isElementEmpty(el) {
    // 没有子节点
    if (el.childNodes.length === 0) {
      return true;
    }
    
    // 检查所有子节点
    let allEmpty = true;
    for (let i = 0; i < el.childNodes.length; i++) {
      const node = el.childNodes[i];
      
      // 文本节点：检查是否只包含空白
      if (node.nodeType === 3) {
        if (node.textContent.trim() !== '') {
          allEmpty = false;
          break;
        }
      }
      // 元素节点：递归检查
      else if (node.nodeType === 1) {
        if (!isElementEmpty(node)) {
          allEmpty = false;
          break;
        }
      }
      // 其他类型的节点（如注释）：视为空
    }
    
    return allEmpty;
  }
  
  // 获取所有元素并检查
  const allElements = doc.querySelectorAll('*');
  const elementsToRemove = [];
  
  allElements.forEach(el => {
    if (isElementEmpty(el)) {
      elementsToRemove.push(el);
    }
  });
  
  // 批量移除元素，避免在遍历时修改DOM
  elementsToRemove.forEach(el => el.remove());
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
