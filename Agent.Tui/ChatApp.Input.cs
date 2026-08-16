using Agent.Tui.Core;

namespace Agent.Tui;

public sealed partial class ChatApp
{
    // ---------- command dispatch ----------

    private const string HelpText = """
        /help            显示本帮助
        /model [query]   选择活动模型(带参数直接匹配,如 /model deepseek)
        /provider        供应商管理:list / add / edit <n> / models <n> / remove <n>
        /new             清空当前会话上下文
        /compact [topic] 手动压缩上下文(可指定主题,如 /compact 项目计划)
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
                StartAsync(() => DoCompactAsync(arg));
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
                AppendChat("sys", $"未知命令: {cmd}(/help 查看命令列表)");
                break;
        }
    }

    /// <summary>把异步任务丢到后台线程;异常统一记录。新渲染模型无跨线程 UI,直接执行即可。</summary>
    private void StartAsync(Func<Task> action)
    {
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
        });
    }

    /// <summary>内联流程(选择器/向导)的后台运行器:整个流程期间置 _modalFlow。</summary>
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
            }
        });
    }
}