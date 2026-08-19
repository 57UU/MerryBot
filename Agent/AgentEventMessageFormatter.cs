using System.Security;
using System.Text;

namespace Agent;

/// <summary>
/// 格式化由后台任务注入 Agent 当前用户输入的事件消息。
/// 使用子元素承载字段，避免事件内容与普通用户文本混在一起；字段值统一转义，防止结果内容破坏标签结构。
/// </summary>
public static class AgentEventMessageFormatter
{
    /// <summary>
    /// 创建一个带 XML 根元素的事件消息。调用方负责保证根元素和字段名是固定的 XML 名称。
    /// </summary>
    public static string Format(string rootName, params (string Name, string? Value)[] fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootName);
        ArgumentNullException.ThrowIfNull(fields);

        StringBuilder message = new();
        message.Append('<').Append(rootName).AppendLine(">");
        foreach ((string name, string? value) in fields)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            string escapedValue = SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
            message.Append('<').Append(name).Append('>')
                .Append(escapedValue)
                .Append("</").Append(name).AppendLine(">");
        }
        message.Append("</").Append(rootName).Append('>');
        return message.ToString();
    }
}
