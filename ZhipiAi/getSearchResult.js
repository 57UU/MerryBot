/**
 * 遍历entry节点，找到第一个a标签和className以b_lineclamp开头的p标签
 * @param {HTMLElement} entryNode - 要遍历的entry节点
 * @returns {Object} - 包含找到的节点的对象
 */
function findSpecificNodes(entryNode) {
  // 存储结果
  const result = {
    firstAnchor: null,
    lineClampParagraph: null
  };

  // 检查是否提供了有效的entry节点
  if (!entryNode || !(entryNode instanceof HTMLElement)) {
    console.error('Invalid entry node provided');
    return result;
  }

  // 创建一个队列用于广度优先搜索
  const queue = [entryNode];

  // 广度优先遍历
  while (queue.length > 0) {
    const currentNode = queue.shift();

    // 检查当前节点是否是a标签
    if (currentNode.tagName && currentNode.tagName.toLowerCase() === 'a' && !result.firstAnchor) {
      result.firstAnchor = currentNode;
    }

    // 检查当前节点是否是p标签且className以b_lineclamp开头
    if (currentNode.tagName && currentNode.tagName.toLowerCase() === 'p' && !result.lineClampParagraph) {
      const classNames = currentNode.className.split(' ');
      if (classNames.some(className => className.startsWith('b_lineclamp'))) {
        result.lineClampParagraph = currentNode;
      }
    }

    // 如果两个节点都找到了，可以提前退出
    if (result.firstAnchor && result.lineClampParagraph) {
      break;
    }

    // 将子节点加入队列
    if (currentNode.childNodes) {
      currentNode.childNodes.forEach(child => {
        if (child.nodeType === 1) { // 只处理元素节点
          queue.push(child);
        }
      });
    }
  }

  return result;
}

table=document.getElementById("b_results")
result=[]
for(let entry of table.childNodes){
    if(entry.className=="b_algo"){
        const specificNodes = findSpecificNodes(entry);
        a=specificNodes.firstAnchor
        p=specificNodes.lineClampParagraph
        result.push({
            title:a.innerText.split("\n")[0],
            description:p.innerHTML,
            link:a.href
        });
    }
}
return JSON.stringify(result)
