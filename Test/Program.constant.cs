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
    const string longLatex="# 卷积定理证明报告\n\n## 一、卷积定理概述\n\n卷积定理是信号处理、图像处理和数学分析中的重要定理，它建立了时域（或空域）卷积与频域乘积之间的对应关系。\n\n### 1.1 定义\n对于两个函数 $f(t)$ 和 $g(t)$，它们的卷积定义为：\n$$(f * g)(t) = \\int_{-\\infty}^{\\infty} f(\\tau)g(t-\\tau) d\\tau$$\n\n### 1.2 卷积定理表述\n设 $\\mathcal{F}$ 表示傅里叶变换算子，则卷积定理可以表述为：\n$$\\mathcal{F}\\{f * g\\} = \\mathcal{F}\\{f\\} \\cdot \\mathcal{F}\\{g\\}$$\n即：两个函数卷积的傅里叶变换等于各自傅里叶变换的乘积。\n\n## 二、连续时间卷积定理证明\n\n### 2.1 傅里叶变换定义\n$$F(\\omega) = \\mathcal{F}\\{f(t)\\} = \\int_{-\\infty}^{\\infty} f(t)e^{-j\\omega t} dt$$\n\n### 2.2 证明过程\n考虑卷积的傅里叶变换：\n$$\\mathcal{F}\\{(f * g)(t)\\} = \\int_{-\\infty}^{\\infty} \\left[ \\int_{-\\infty}^{\\infty} f(\\tau)g(t-\\tau) d\\tau \\right] e^{-j\\omega t} dt$$\n\n交换积分次序（假设满足Fubini定理条件）：\n$$= \\int_{-\\infty}^{\\infty} f(\\tau) \\left[ \\int_{-\\infty}^{\\infty} g(t-\\tau)e^{-j\\omega t} dt \\right] d\\tau$$\n\n令 $u = t - \\tau$，则 $t = u + \\tau$，$dt = du$：\n$$= \\int_{-\\infty}^{\\infty} f(\\tau) \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega (u+\\tau)} du \\right] d\\tau$$\n$$= \\int_{-\\infty}^{\\infty} f(\\tau)e^{-j\\omega \\tau} \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega u} du \\right] d\\tau$$\n$$= \\left[ \\int_{-\\infty}^{\\infty} f(\\tau)e^{-j\\omega \\tau} d\\tau \\right] \\cdot \\left[ \\int_{-\\infty}^{\\infty} g(u)e^{-j\\omega u} du \\right]$$\n$$= F(\\omega) \\cdot G(\\omega)$$\n\n### 2.3 证明完成\n因此得到：\n$$\\mathcal{F}\\{f * g\\} = F(\\omega)G(\\omega)$$\n\n## 三、离散时间卷积定理证明\n\n### 3.1 离散傅里叶变换定义\n对于长度为 $N$ 的序列 $x[n]$ 和 $h[n]$：\n$$X[k] = \\sum_{n=0}^{N-1} x[n]e^{-j\\frac{2\\pi}{N}kn}$$\n\n### 3.2 离散卷积定义\n$$y[n] = (x * h)[n] = \\sum_{m=0}^{N-1} x[m]h[(n-m)_N]$$\n其中 $(n-m)_N$ 表示模 $N$ 运算。\n\n### 3.3 证明过程\n计算 $y[n]$ 的DFT：\n$$Y[k] = \\sum_{n=0}^{N-1} y[n]e^{-j\\frac{2\\pi}{N}kn}$$\n$$= \\sum_{n=0}^{N-1} \\left[ \\sum_{m=0}^{N-1} x[m]h[(n-m)_N] \\right] e^{-j\\frac{2\\pi}{N}kn}$$\n\n交换求和次序：\n$$= \\sum_{m=0}^{N-1} x[m] \\left[ \\sum_{n=0}^{N-1} h[(n-m)_N] e^{-j\\frac{2\\pi}{N}kn} \\right]$$\n\n令 $l = (n-m)_N$，则 $n = (l+m)_N$：\n$$= \\sum_{m=0}^{N-1} x[m] \\left[ \\sum_{l=0}^{N-1} h[l] e^{-j\\frac{2\\pi}{N}k(l+m)} \\right]$$\n$$= \\sum_{m=0}^{N-1} x[m]e^{-j\\frac{2\\pi}{N}km} \\cdot \\sum_{l=0}^{N-1} h[l]e^{-j\\frac{2\\pi}{N}kl}$$\n$$= X[k] \\cdot H[k]$$\n\n## 四、卷积定理的逆定理\n\n### 4.1 时域乘积定理\n$$\\mathcal{F}\\{f(t) \\cdot g(t)\\} = \\frac{1}{2\\pi} F(\\omega) * G(\\omega)$$\n\n### 4.2 证明思路\n利用傅里叶变换的对称性和卷积定理的证明方法，可以类似证明。\n\n## 五、卷积定理的应用\n\n### 5.1 信号处理\n- 线性时不变系统的频域分析\n- 滤波器的设计与实现\n- 信号去噪和增强\n\n### 5.2 图像处理\n- 图像滤波（高斯滤波、中值滤波等）\n- 边缘检测（Sobel、Canny算子）\n- 图像锐化和模糊\n\n### 5.3 通信系统\n- 调制解调\n- 信道均衡\n- 多径效应分析\n\n## 六、证明中的数学要点\n\n### 6.1 积分交换条件\n需要满足Fubini定理条件：\n1. 函数绝对可积\n2. 积分区域有限或函数有界\n\n### 6.2 收敛性\n- 对于连续时间，需要函数平方可积\n- 对于离散时间，需要序列能量有限\n\n### 6.3 周期性处理\n离散卷积需要考虑循环卷积和线性卷积的区别\n\n## 七、总结\n\n卷积定理是傅里叶分析的核心定理之一，它将复杂的时域卷积运算转化为简单的频域乘积运算，大大简化了系统分析和信号处理的计算复杂度。该定理在工程实践和理论研究中都有广泛应用。\n\n**证明要点总结：**\n1. 利用傅里叶变换的线性性\n2. 通过变量替换分离变量\n3. 交换积分次序（需满足收敛条件）\n4. 利用傅里叶变换的定义完成证明";
}