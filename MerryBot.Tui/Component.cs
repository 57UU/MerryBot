namespace Agent.Tui.Core;

/// <summary>
/// 组件契约（借鉴 pi 的 Component 接口，裁剪为 C# 版）。
/// - <see cref="Render"/> 纯函数：给定可用宽度，返回渲染后的行数组（每行含 ANSI 样式）。
/// - <see cref="HandleInput"/>：焦点在本组件时收到的一次按键事件。
/// - <see cref="Invalidate"/>：清空内部缓存，下次 Render 强制重算。
/// 渲染层不缓存组件内容，差分渲染只比较最后输出到终端的行。
/// </summary>
public interface IComponent
{
    /// <summary>渲染到给定宽度（列数），返回行数组（含 ANSI 样式，不含换行符）。</summary>
    string[] Render(int width);

    /// <summary>焦点在本组件时处理一次输入；返回 true 表示已消费。</summary>
    bool HandleInput(KeyEvent ev);

    /// <summary>清空缓存；主题/内容外部变化后调用。</summary>
    void Invalidate();
}

/// <summary>可聚焦组件的标记：渲染时可输出光标位置标记，供外层定位硬件光标。</summary>
public interface IFocusable
{
    /// <summary>是否拥有焦点（由 TUI 设置）。</summary>
    bool IsFocused { get; set; }
}

/// <summary>基础组件：统一管理 Invalidate 的默认实现。</summary>
public abstract class ComponentBase : IComponent
{
    public abstract string[] Render(int width);
    public abstract bool HandleInput(KeyEvent ev);
    public virtual void Invalidate() { }
}

/// <summary>
/// 容器组件：按竖排顺序渲染子组件，行直接拼接。
/// 它不负责滚动——ChatApp 的聊天区由专用滚动容器处理。
/// </summary>
public class Container : ComponentBase
{
    private readonly List<IComponent> _children = [];

    public IReadOnlyList<IComponent> Children => _children;

    public void Add(IComponent child) => _children.Add(child);
    public void Clear() => _children.Clear();

    public override string[] Render(int width)
    {
        var lines = new List<string>();
        foreach (var child in _children)
        {
            lines.AddRange(child.Render(width));
        }
        return lines.ToArray();
    }

    public override bool HandleInput(KeyEvent ev)
    {
        foreach (var child in _children.OfType<IFocusable>())
        {
            if (child.IsFocused && child is IComponent c)
            {
                return c.HandleInput(ev);
            }
        }
        return false;
    }

    public override void Invalidate()
    {
        foreach (var child in _children)
        {
            child.Invalidate();
        }
    }
}