const retain_a_tag=false;

/**
 * 判断href是否为正常网址
 * @param {string} href - 要检查的href值
 * @returns {boolean} - 如果是正常网址返回true，否则返回false
 */
function isNormalUrl(href) {
  if (!href) return false;
  
  // 忽略javascript:、mailto:、tel:等协议
  const invalidProtocols = ['javascript:', 'mailto:', 'tel:', 'sms:', 'ftp:', 'file:'];
  for (const protocol of invalidProtocols) {
    if (href.toLowerCase().startsWith(protocol)) {
      return false;
    }
  }
  
  // 忽略javascript:void(0)和类似的javascript表达式
  if (href.toLowerCase() === 'javascript:void(0)' || href.toLowerCase() === 'javascript:void(0);') {
    return false;
  }
  
  // 接受http://、https://开头的绝对URL
  // 接受相对路径（以/、./、../开头）
  // 接受以www.开头的URL
  return (
    href.startsWith('http://') || 
    href.startsWith('https://') || 
    href.startsWith('/') || 
    href.startsWith('./') || 
    href.startsWith('../') ||
    href.startsWith('www.')
  );
}

/**
 * 获取页面中所有a标签的href属性（如果retain_a_tag为true）
 * @returns {Array<string>} - 包含所有href属性值的数组
 */
function extractAllHrefs() {
  // 如果不保留a标签，直接返回空数组
  if (!retain_a_tag) {
    return [];
  }
  
  // 创建一个空数组存储href值
  const hrefs = [];

  // 获取页面中所有的a标签
  const aTags = document.querySelectorAll('a');

  // 遍历所有a标签，提取href属性
  aTags.forEach(aTag => {
    const href = aTag.getAttribute('href');
    if (href && isNormalUrl(href)) {
      // 确保href为正常网址
      hrefs.push({
        href,
        text: aTag.textContent.trim()
      });
    }
  });

  return hrefs;
}

/**
 * 获取净化后的HTML，只保留可见文本和a标签的href属性（如果retain_a_tag为true）
 * @returns {string} - 净化后的HTML
 */
function getCleanHTML() {
  removeUnnecessaryElement();
  // 克隆一份文档，避免修改原页面
  const clonedDoc = document.documentElement.cloneNode(true);

  // 移除所有不可见元素和不需要的标签
  removeInvisibleElements(clonedDoc);
  
  
  if (retain_a_tag) {
    // 清理所有标签的属性，只保留a标签的href属性
    cleanAttributes(clonedDoc);
  } else {
    // 如果不保留a标签，移除所有a标签
    removeAllATags(clonedDoc);
  }

  // 返回净化后的HTML
  return cleanHtmlTags(clonedDoc.outerHTML);
}
function removeUnnecessaryElement(){
  let url= window.location.href;
  if(url.startsWith("https://github.com")){
    tryRemove("body > div.logged-out.env-production.page-responsive.page-profile > div.position-relative.header-wrapper.js-header-wrapper")
    tryRemove("#user-profile-frame > div > div.mt-4.position-relative > div > div.col-12.col-lg-10 > div.js-yearly-contributions > div:nth-child(1)")
    tryRemove("body > div.logged-out.env-production.page-responsive.header-overlay.header-overlay-fixed.js-header-overlay-fixed > div.position-relative.header-wrapper.js-header-wrapper > header")
    tryRemove("body > div.logged-out.env-production.page-responsive > div.position-relative.header-wrapper.js-header-wrapper > header")
  }
}
function tryRemove(selector){
  let element=document.querySelector(selector);
  if(element){
    element.remove();
  }
}

function cleanHtmlTags(html) {
  if (!retain_a_tag) {
    // 如果不保留a标签，移除所有HTML标签
    html = html.replace(/<[^>]+>/g, '|');
  } else {
    // 移除除a标签外的所有HTML标签
    html = html.replace(/<(?!a\b)[^>]+>/g, '|');
  }
  
  // 解码HTML实体
  const textarea = document.createElement('textarea');
  textarea.innerHTML = html;
  html = textarea.value;
  
  // 将连续的竖线或空格替换为单个竖线
  html = html.replace(/[\|\s][\|\s\n]*[\|\s]/g, '|');
  return html;
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

  // 移除含有data-nosnippet属性的元素
  doc.querySelectorAll('[data-nosnippet]').forEach(el => el.remove());

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
 * 移除文档中的所有a标签，将其内容保留
 * @param {HTMLElement} doc - 文档元素
 */
function removeAllATags(doc) {
  const aTags = doc.querySelectorAll('a');
  const aTagsArray = Array.from(aTags);
  
  aTagsArray.forEach(aTag => {
    // 保留a标签的内容，将子节点移动到父节点
    while (aTag.firstChild) {
      aTag.parentNode.insertBefore(aTag.firstChild, aTag);
    }
    // 移除a标签
    aTag.parentNode.removeChild(aTag);
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

    // 为a标签恢复href属性，但只保留正常网址
    if (el.tagName.toLowerCase() === 'a' && href && isNormalUrl(href)) {
      el.setAttribute('href', href);
    }
  });
}

// 返回净化后的HTML
return getCleanHTML();

// 如果需要仅获取href数组，可以使用以下行
// return extractAllHrefs();
