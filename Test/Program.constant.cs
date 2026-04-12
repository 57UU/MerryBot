public static partial class Program{
    const string regularMarkdown=@"
# 🌙 MerryBot Markdown 全能测试报告

> **时间**: 2026-04-11
> **状态**: 测试中
> **版本**: v1.0.0

---

## 🛠️ 核心功能展示

### 1. 样式与格式
你可以轻松地使用 **加粗**、*斜体*、~~删除线~~ 以及 `行内代码`。
还可以通过以下列表组织内容：

- [x] 自动高度截图
- [x] 多级标题支持
- [x] 实时代码高亮
- [ ] 外部图片加载
- [ ] 交互式图表 (计划中)

---

### 2. 结构化数据 (表格)

| 功能模块 | 状态 | 优先级 | 备注 |
| :--- | :---: | :---: | :--- |
| 浏览器渲染 | ✅ 正常 | 高 | 支持 Chrome/Edge |
| Markdown 转换 | ✅ 正常 | 高 | 基于 Markdig 引擎 |
| 自动缩放 | ✅ 正常 | 中 | DeviceScaleFactor: 1.5 |
| 高度自适应 | ✅ 正常 | 高 | 滚动高度计算 |

---

### 3. 代码块

```csharp
// C# 代码片段测试
public class MerryBot
{
    public string Name { get; set; } = ""Merry"";
    public async Task Greet()
    {
        Console.WriteLine($""Hello from {Name}!"");
    }
}
```
---
### 4. 引用与嵌套
> 这是一级引用
> > 这是一个嵌套的二级引用
> > 
> > - 引用中的列表项 1
> > - 引用中的列表项 2

";

    const string mathAndMermaidMarkdown = @"
# 📊 MerryBot 公式与图表测试报告

---

## 🧬 数学公式 (MathJax)

### 1. 行内公式
这是一个著名的质能方程：$ E = mc^2 $。
勾股定理：$ a^2 + b^2 = c^2 $。

### 2. 独立块公式
麦克斯韦方程组之一：
$$ \nabla \cdot \mathbf{E} = \frac{\rho}{\epsilon_0} $$

二次方程求根公式：
$$ x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a} $$

---

## 🗺️ 流程图与图表 (Mermaid)

### 1. 基础流程图
```mermaid
graph TD
    A[开始] --> B{是否联网?}
    B -- 是 --> C[加载 CDN 资源]
    B -- 否 --> D[加载本地 JS 资源]
    C --> E[渲染完成]
    D --> E
    E --> F[保存截图]
```

### 2. 时序图
```mermaid
sequenceDiagram
    participant User as 用户
    participant Bot as MerryBot
    participant Browser as 浏览器
    User->>Bot: 发送 Markdown
    Bot->>Browser: 注入 HTML + JS
    Browser->>Browser: 异步渲染 (Mermaid/Math)
    Browser-->>Bot: window.renderComplete = true
    Bot->>Browser: 执行截图
    Browser-->>User: 返回图片
```

### 3. 甘特图
```mermaid
gantt
    title 离线渲染功能开发进度
    dateFormat  YYYY-MM-DD
    section 准备阶段
    资源下载           :done,    des1, 2026-04-10, 1d
    section 开发阶段
    模板修改           :active,  des2, 2026-04-11, 2d
    后端逻辑调整       :         des3, after des2, 2d
    section 测试阶段
    功能验证           :         des4, after des3, 1d
```
";
    const string longLatex="# 卷积定理证明报告\n\n## 1. 卷积定理定义\n\n卷积定理指出：两个函数卷积的傅里叶变换等于各自傅里叶变换的乘积。\n\n数学表达为：\n$$\n\\mathcal{F}\\{f(t) * g(t)\\} = F(\\omega) \\cdot G(\\omega)\n$$\n\n其中：\n- $f(t)$ 和 $g(t)$ 是两个时域函数\n- $*$ 表示卷积运算：$(f * g)(t) = \\int_{-\\infty}^{\\infty} f(\\tau)g(t-\\tau)d\\tau$\n- $F(\\omega) = \\mathcal{F}\\{f(t)\\}$ 是 $f(t)$ 的傅里叶变换\n- $G(\\omega) = \\mathcal{F}\\{g(t)\\}$ 是 $g(t)$ 的傅里叶变换\n\n## 2. 连续时间卷积定理证明\n\n### 2.1 证明过程\n\n设 $h(t) = (f * g)(t) = \\int_{-\\infty}^{\\infty} f(\\tau)g(t-\\tau)d\\tau$\n\n对 $h(t)$ 进行傅里叶变换：\n\n$$\n\\begin{aligned}\nH(\\omega) &= \\mathcal{F}\\{h(t)\\} \\\\\n&= \\int_{-\\infty}^{\\infty} h(t)e^{-j\\omega t}dt \\\\\n&= \\int_{-\\infty}^{\\infty} \\left[ \\int_{-\\infty}^{\\infty} f(\\tau)g(t-\\tau)d\\tau \\right] e^{-j\\omega t}dt\n\\end{aligned}\n$$\n\n交换积分次序：\n\n$$\n\\begin{aligned}\nH(\\omega) &= \\int_{-\\infty}^{\\infty} f(\\tau) \\left[ \\int_{-\\infty}^{\\infty} g(t-\\tau)e^{-j\\omega t}dt \\right] d\\tau\n\\end{aligned}\n$$\n\n令 $u = t - \\tau$，则 $t = u + \\tau$，$dt = du$：\n\n$$\n\\begin{aligned}\nH(\\omega) &= \\int_{-\\infty}^{\\infty} f(\\tau) \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega (u+\\tau)}du \\right] d\\tau \\\\\n&= \\int_{-\\infty}^{\\infty} f(\\tau)e^{-j\\omega \\tau} \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega u}du \\right] d\\tau \\\\\n&= \\left[ \\int_{-\\infty}^{\\infty} f(\\tau)e^{-j\\omega \\tau}d\\tau \\right] \\cdot \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega u}du \\right] \\\\\n&= F(\\omega) \\cdot G(\\omega)\n\\end{aligned}\n$$\n\n**证毕。**\n\n## 3. 离散时间卷积定理证明\n\n对于离散序列：\n\n$$\n\\mathcal{F}\\{x[n] * h[n]\\} = X(e^{j\\omega}) \\cdot H(e^{j\\omega})\n$$\n\n其中离散卷积定义为：\n$$\n(x * h)[n] = \\sum_{k=-\\infty}^{\\infty} x[k]h[n-k]\n$$\n\n### 3.1 证明过程\n\n设 $y[n] = (x * h)[n] = \\sum_{k=-\\infty}^{\\infty} x[k]h[n-k]$\n\n对 $y[n]$ 进行离散时间傅里叶变换：\n\n$$\n\\begin{aligned}\nY(e^{j\\omega}) &= \\sum_{n=-\\infty}^{\\infty} y[n]e^{-j\\omega n} \\\\\n&= \\sum_{n=-\\infty}^{\\infty} \\left[ \\sum_{k=-\\infty}^{\\infty} x[k]h[n-k] \\right] e^{-j\\omega n}\n\\end{aligned}\n$$\n\n交换求和次序：\n\n$$\n\\begin{aligned}\nY(e^{j\\omega}) &= \\sum_{k=-\\infty}^{\\infty} x[k] \\left[ \\sum_{n=-\\infty}^{\\infty} h[n-k]e^{-j\\omega n} \\right]\n\\end{aligned}\n$$\n\n令 $m = n - k$，则 $n = m + k$：\n\n$$\n\\begin{aligned}\nY(e^{j\\omega}) &= \\sum_{k=-\\infty}^{\\infty} x[k] \\left[ \\sum_{m=-\\infty}^{\\infty} h[m]e^{-j\\omega (m+k)} \\right] \\\\\n&= \\sum_{k=-\\infty}^{\\infty} x[k]e^{-j\\omega k} \\left[ \\sum_{m=-\\infty}^{\\infty} h[m]e^{-j\\omega m} \\right] \\\\\n&= \\left[ \\sum_{k=-\\infty}^{\\infty} x[k]e^{-j\\omega k} \\right] \\cdot \\left[ \\sum_{m=-\\infty}^{\\infty} h[m]e^{-j\\omega m} \\right] \\\\\n&= X(e^{j\\omega}) \\cdot H(e^{j\\omega})\n\\end{aligned}\n$$\n\n**证毕。**\n\n---\n\n*报告生成时间：2026-04-12 19:43*\n*生成者：曼瑞 (川大本科，开发岗社畜)*";
}