using Agent;
using System.Xml.Linq;

namespace MerryBot.Test;

public sealed class AgentEventMessageFormatterTests
{
    [Fact]
    public void TerminalResult_UsesChildElementsAndEscapesValues()
    {
        string xml = AgentEventMessageFormatter.Format(
            "TERMINAL_TASK_RESULT",
            ("task_id", "abc&123"),
            ("status", "completed"),
            ("description", "构建 <MerryBot>"),
            ("output", "成功 & 通过"));

        XElement root = XElement.Parse(xml);
        Assert.Equal("TERMINAL_TASK_RESULT", root.Name.LocalName);
        Assert.Equal("abc&123", (string?)root.Element("task_id"));
        Assert.Equal("completed", (string?)root.Element("status"));
        Assert.Equal("构建 <MerryBot>", (string?)root.Element("description"));
        Assert.Equal("成功 & 通过", (string?)root.Element("output"));
        Assert.Contains("<description>构建 &lt;MerryBot&gt;</description>", xml);
        Assert.Contains("<output>成功 &amp; 通过</output>", xml);
        Assert.DoesNotContain("task_id=", xml);
    }

    [Fact]
    public void SubagentFailure_ContainsFailureAndErrorFields()
    {
        string xml = AgentEventMessageFormatter.Format(
            "SUBAGENT_RESULT",
            ("task_id", "task-1"),
            ("status", "failed"),
            ("task", "分析 </task>"),
            ("error", "错误: x & y"));

        XElement root = XElement.Parse(xml);
        Assert.Equal("SUBAGENT_RESULT", root.Name.LocalName);
        Assert.Equal("task-1", (string?)root.Element("task_id"));
        Assert.Equal("failed", (string?)root.Element("status"));
        Assert.Equal("分析 </task>", (string?)root.Element("task"));
        Assert.Equal("错误: x & y", (string?)root.Element("error"));
        Assert.Null(root.Element("output"));
        Assert.Contains("<task>分析 &lt;/task&gt;</task>", xml);
        Assert.Contains("<error>错误: x &amp; y</error>", xml);
    }
}
