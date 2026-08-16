using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- UI helpers ----------

    private void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            action();
        }
        else
        {
            _app.Invoke(action);
        }
    }

    private void AppendChat(string role, string text)
    {
        Invoke(() =>
        {
            var chatRole = RoleOf(role);
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            // 只有首行带 emoji 前缀，续行按前缀显示宽度对齐缩进
            var prefix = RolePrefix(role);
            var indent = new string(' ', TextWidth(prefix));
            for (int i = 0; i < lines.Length; i++)
            {
                _chatSource.Add(i == 0 ? prefix + lines[i] : indent + lines[i]);
                _chatRoles.Add(chatRole);
            }
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    /// <summary>追加一行无前缀的原始文本（带角色颜色），用于工具结果摘要等。</summary>
    private void AppendLine(ChatRole role, string text)
    {
        Invoke(() =>
        {
            _chatSource.Add(text);
            _chatRoles.Add(role);
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    private void AppendDebug(string line)
    {
        if (!_debug)
        {
            return;
        }
        Invoke(() =>
        {
            _chatSource.Add(line);
            _chatRoles.Add(ChatRole.Debug);
            if (_chatSource.Count > 0)
            {
                _chat!.SelectedItem = _chatSource.Count - 1;
            }
        });
    }

    private void RefreshStatus()
    {
        Invoke(() =>
        {
            var (p, m) = _cfg.ResolveActive();
            var usage = _session?.SessionUsage;
            var tokens = usage?.totalUsage ?? 0;
            var cache = usage is { promptUsage: > 0 }
                ? $" | cache: {usage.cachedUsage * 100.0 / usage.promptUsage:0}%"
                : string.Empty;
            var queue = Volatile.Read(ref _pendingCount) > 0 ? $" | queue: {_pendingCount}" : string.Empty;
            _status!.Text = $"model: {m ?? "-"} | provider: {p?.Name ?? "-"} | debug: {(_debug ? "on" : "off")} | tokens: {tokens}{cache}{queue}";
        });
    }

    private void SetInputEnabled(bool enabled)
    {
        Invoke(() =>
        {
            _input!.Enabled = enabled;
            if (enabled)
            {
                _input.SetFocus();
            }
        });
    }

    /// <summary>点击窗口空白处/聊天区时，把焦点还给输入框（滚轮滚动不受影响）。</summary>
    private void OnBlankClick(Mouse e)
    {
        if (e.IsSingleClicked)
        {
            e.Handled = true;
            _input!.SetFocus();
        }
    }

    /// <summary>把展示用的角色名映射为行角色，用于着色。</summary>
    private static ChatRole RoleOf(string role) => role switch
    {
        "You" => ChatRole.User,
        "Assistant" => ChatRole.Assistant,
        "error" => ChatRole.Error,
        "Cron" => ChatRole.Cron,
        "tool" => ChatRole.Tool,
        _ => ChatRole.System,
    };

    /// <summary>角色对应的 emoji 前缀。</summary>
    private static string RolePrefix(string role) => role switch
    {
        "You" => "⭐ ",
        "Assistant" => "● ",
        "tool" => "● ",
        "error" => "✗ ",
        "Cron" => "⏰ ",
        _ => "· ", // sys 等
    };

    private static Color RoleColor(ChatRole role) => role switch
    {
        ChatRole.User => Color.Yellow, // 金黄色
        ChatRole.Assistant => Color.White,
        ChatRole.Error => Color.Red,
        ChatRole.Cron => Color.Yellow,
        ChatRole.Tool => Color.Green,
        _ => Color.DarkGray,
    };

    /// <summary>按终端显示宽度计算（emoji/全角=2 列，ASCII=1 列）。</summary>
    private static int TextWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.GetColumns();
        }
        return width;
    }
    /// <summary>
    /// 单一前景色、终端默认背景（Color.None）的 Scheme，用于状态栏/提示符等弱化或强调元素。
    /// 关键点：显式设置所有视觉角色（Focus / Editable / Active 等）为同一个 Attribute，
    /// 禁用 Terminal.Gui 的派生算法——否则 Focus 会交换 fg/bg、Editable 会把背景设为前景的 dim 50%，
    /// 导致 TextField 聚焦时出现灰色/反色底色，破坏"仅外边框"的扁平视觉。
    /// </summary>
    private static Scheme SingleColorScheme(Color foreground)
    {
        var attr = new Attribute(foreground, Color.None);
        return new Scheme
        {
            Normal = attr,
            HotNormal = attr,
            Focus = attr,
            HotFocus = attr,
            Active = attr,
            HotActive = attr,
            Highlight = attr,
            Editable = attr,
            ReadOnly = attr,
            Disabled = attr,
        };
    }
}
