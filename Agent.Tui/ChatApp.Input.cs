using Agent.Tui.Views;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- command dispatch ----------

    private const string HelpText = """
        /help            显示本帮助
        /model [query]   选择活动模型（带参数直接匹配，如 /model deepseek）
        /provider        供应商管理：list / add / edit <n> / models <n> / remove <n>
        /new             清空当前会话上下文
        /compact         手动压缩上下文
        /stop            取消当前对话
        /debug           切换调试日志
        /refresh         重新拉取 models.dev 目录
        /status          显示当前配置摘要
        /exit            退出
        """;

    private void DispatchInput(string raw)
    {
        var input = raw.Trim();
        if (input.Length == 0)
        {
            return;
        }
        if (input[0] != '/')
        {
            QueueChat(input);
            return;
        }

        var rest = input[1..];
        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = sp.Length > 0 ? sp[0].ToLowerInvariant() : string.Empty;
        var arg = sp.Length > 1 ? sp[1].Trim() : string.Empty;

        switch (cmd)
        {
            case "help":
                AppendChat("sys", HelpText);
                break;
            case "model":
                RunFlow(() => OpenModelPickerAsync(arg));
                break;
            case "provider":
                RunFlow(() => RunProviderCommandAsync(arg));
                break;
            case "new":
                StartAsync(DoNewAsync);
                break;
            case "compact":
                StartAsync(DoCompactAsync);
                break;
            case "stop":
                DoStop();
                break;
            case "debug":
                _debug = !_debug;
                AppendChat("sys", $"[debug] {(_debug ? "已开启" : "已关闭")}");
                break;
            case "refresh":
                StartAsync(DoRefreshAsync);
                break;
            case "status":
                DoStatus();
                break;
            case "exit":
                _app.RequestStop();
                break;
            default:
                AppendChat("sys", $"未知命令: {cmd}（/help 查看命令列表）");
                break;
        }
    }

    /// <summary>把异步任务丢到后台线程，执行期间禁用输入框；异常统一记录。</summary>
    private void StartAsync(Func<Task> action)
    {
        SetInputEnabled(false);
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetInputEnabled(true);
            }
        });
    }

    /// <summary>
    /// 内联流程（选择器/向导）的后台运行器：整个流程期间置 <see cref="_modalFlow"/>，
    /// 吞掉输入行的普通命令（提示模式除外），结束统一恢复输入。
    /// </summary>
    private void RunFlow(Func<Task> action)
    {
        _modalFlow = true;
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _modalFlow = false;
                SetInputEnabled(true);
            }
        });
    }

    /// <summary>输入行回车：优先结束提示模式，其次执行命令；流程进行中吞掉普通输入。</summary>
    private void OnInputAccepting(object? sender, CommandEventArgs e)
    {
        e.Handled = true;
        var text = _input!.Text;
        _input.Text = string.Empty;
        if (_promptTcs is { } tcs)
        {
            _promptTcs = null;
            var value = string.IsNullOrEmpty(text) ? _promptDefault : text.Trim();
            tcs.TrySetResult(value);
            _promptLabel!.Text = PromptIdleText;
            return;
        }
        if (_modalFlow)
        {
            return; // 选择器/向导进行中，普通输入不生效
        }
        DispatchInput(text ?? string.Empty);
    }

    /// <summary>输入行 Esc：提示模式中取消当前提问。</summary>
    private void OnInputKeyDown(object? sender, Key key)
    {
        if (_promptTcs is { } tcs && key == Key.Esc)
        {
            key.Handled = true;
            _promptTcs = null;
            tcs.TrySetResult(null);
            _promptLabel!.Text = PromptIdleText;
            _input!.Text = string.Empty;
        }
    }

    /// <summary>提示模式：在输入行显示问题，等待一次 Enter（空输入用默认值，Esc 返回 null）。</summary>
    private Task<string?> PromptAsync(string question, string? defaultValue)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() =>
        {
            _promptTcs = tcs;
            _promptDefault = defaultValue;
            _promptLabel!.Text = question;
            _input!.Text = defaultValue ?? string.Empty;
            SetInputEnabled(true);
        });
        return tcs.Task;
    }

    /// <summary>挂载并等待一个内联选择器；结束后移除并恢复输入框。</summary>
    private async Task<List<PickList.Item>?> PickAsync(PickList picker)
    {
        Invoke(() =>
        {
            SetInputEnabled(false);
            _window!.Add(picker);
            picker.FocusFilter();
        });
        var result = await picker.WaitAsync();
        Invoke(() =>
        {
            _window!.Remove(picker);
            SetInputEnabled(true);
            _input!.SetFocus();
        });
        return result;
    }
}
