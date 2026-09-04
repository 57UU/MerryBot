using System.Text;
using Agent.Tui.Lib;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- UI helpers ----------

    private void AppendChat(string role, string text)
    {
        var chatRole = RoleOf(role);
        var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        // 只有首行带 emoji 前缀,续行按前缀显示宽度对齐缩进
        var prefix = RolePrefix(role);
        var indent = new string(' ', TextWidth.Measure(prefix));
        for (int i = 0; i < lines.Length; i++)
        {
            AppendRoleLine(chatRole, i == 0 ? prefix + lines[i] : indent + lines[i]);
        }
        Invalidate();
    }

    /// <summary>追加一行无前缀的原始文本(带角色颜色),用于工具结果摘要等。</summary>
    private void AppendLine(ChatRole role, string text)
    {
        AppendRoleLine(role, text);
        Invalidate();
    }

    private void AppendDebug(string line)
    {
        if (!_debug) return;
        AppendRoleLine(ChatRole.Debug, line);
        Invalidate();
    }

    private void AppendRoleLine(ChatRole role, string text)
    {
        var colored = RoleColorApply(role, text);
        _chat.Append(colored);
    }

    /// <summary>按角色给行上色,返回带 ANSI 的行。输入文本先剥离 ESC 序列(防终端注入)。</summary>
    private static string RoleColorApply(ChatRole role, string text)
    {
        // 安全:聊天区/思考面板的文本来自 LLM/工具/用户,属不可信数据。
        // 自研渲染直接输出 ANSI,若不过滤,内容里的 ESC 序列会直通终端(可伪造 UI/注入按键)。
        // 只允许本层 Ansi.* 包装注入样式,内容本身剥掉全部转义序列。
        var safe = Ansi.StripAnsi(text ?? string.Empty).ToString();
        return role switch
        {
            ChatRole.User => Ansi.Wrap(Ansi.Fg(3), safe),    // 黄色
            ChatRole.Assistant => Ansi.Wrap(Ansi.Fg(7), safe), // 白色
            ChatRole.Error => Ansi.Wrap(Ansi.Fg(1), safe),   // 红色
            ChatRole.Cron => Ansi.Wrap(Ansi.Fg(3), safe),
            ChatRole.Tool => Ansi.Wrap(Ansi.Fg(2), safe),    // 绿色
            _ => Ansi.Wrap(Ansi.Dim, safe),                 // 系统灰
        };
    }

    private static ChatRole RoleOf(string role) => role switch
    {
        "You" => ChatRole.User,
        "Assistant" => ChatRole.Assistant,
        "error" => ChatRole.Error,
        "Cron" => ChatRole.Cron,
        "tool" => ChatRole.Tool,
        _ => ChatRole.System,
    };

    private static string RolePrefix(string role) => role switch
    {
        "You" => "⭐ ",
        "Assistant" => "● ",
        "tool" => "● ",
        "error" => "✗ ",
        "Cron" => "⏰ ",
        _ => "· ",
    };

    /// <summary>单行思考面板:覆盖为最近一段内容(原有滚动展示劣化为单行,节奏更快)。</summary>
    private void SetPaneLine(string text)
    {
        // 安全:思考面板内容来自 LLM 中间输出,剥离 ESC 防终端注入
        var clean = Ansi.StripAnsi(text ?? string.Empty).ToString()
            .Replace("\r", string.Empty).Replace("\n", " ");
        _paneLine = clean;
        if (_paneLine.Length > Console.WindowWidth - 4)
        {
            _paneLine = TextWidth.Truncate(_paneLine, Math.Max(10, Console.WindowWidth - 7), "…");
        }
        Invalidate();
    }

    private void RefreshStatus() => Invalidate();
}