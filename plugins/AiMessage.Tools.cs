using NapcatClient.MessageType;
using System.Runtime.InteropServices;
using OpenAiClient;


namespace BotPlugin;

public partial class AiMessage
{
    void RegisterVoiceTool()
    {
        var voiceSender = new ToolDef();
        voiceSender.Function.Name = "send_voice";
        voiceSender.HideOutputOnInvoking = true;
        voiceSender.Function.Description = "发送语音/唱歌";
        voiceSender.Function.Parameters.AddRequired("text", new ParameterProperty() { Type = "string", Description = "要发送成语言的内容" });
        voiceSender.Function.FunctionCall = async (parameters) =>
        {
            try
            {
                rateLimiter.Increase(parameters.SpecialTag);
                if (rateLimiter.CheckIsLimited(parameters.SpecialTag))
                {
                    throw new Exception("请求速率过高，请不要再发了");
                }
                string text = parameters["text"].GetString()!;
                await Actions.SendGroupAiVoice(parameters.SpecialTag.ToString(), text);
            }
            catch (Exception e)
            {
                return $"发送失败:{e.Message}";
            }
            return "发送成功。你不必回复'已发送',也不必重复发送的信息";
        };
        aiClient.RegisterTool(voiceSender);
    }

    void RegisterReplyTool()
    {
        var replyTool = new ToolDef();
        replyTool.Function.Name = "reply";
        replyTool.Function.Description = "回复消息";
        replyTool.DynamicPrompt = "需要发送消息时，使用reply工具";
        replyTool.HideOutputOnInvoking = true;
        replyTool.Function.Parameters.AddRequired("text", new ParameterProperty() { Type = "string", Description = "要回复的内容" });
        replyTool.Function.FunctionCall = async (parameters) =>
        {
            try
            {
                messageRateLimiter.Increase(parameters.SpecialTag);
                if (messageRateLimiter.CheckIsLimited(parameters.SpecialTag))
                {
                    throw new Exception("请求速率过高，请不要再发了");
                }
                string text = parameters["text"].GetString()!;
                if (!string.IsNullOrEmpty(text))
                {
                    await Actions.SendGroupMessage(parameters.SpecialTag, text);
                }
            }
            catch (Exception e)
            {
                return $"发送失败:{e.Message}";
            }
            return "成功";
        };
        aiClient.RegisterTool(replyTool);
    }

    void RegisterImagePainterTool()
    {
        var imagePainterTool = new ToolDef();
        imagePainterTool.Function.Name = "draw_image";
        imagePainterTool.Function.Description = "绘制图片";
        imagePainterTool.Function.Parameters.AddRequired("prompt", new ParameterProperty() { Type = "string", Description = "要绘制的图片描述" });
        imagePainterTool.Function.FunctionCall = async (parameters) =>
        {
            string prompt = parameters["prompt"].GetString()!;
            _ = DrawImageAndSend(prompt, parameters.SpecialTag);
            return "正在绘制中...";
        };
        aiClient.RegisterTool(imagePainterTool);
    }

    void RegisterShellTool()
    {
        var user = Interop.GetVariableOrSetDefault("shell-user", "merrybot");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var shellManager = new ShellManager(user);
            var skillsPrompt = BuildSkillsPrompt(user);

            var shell = new ToolDef();
            shell.Function.Name = "shell";
            shell.DynamicPrompt = $"你可以使用shell工具在你的linux电脑上执行命令，已安装py等程序，user: {user}。命令将在后台执行，使用shell_result查询结果。{skillsPrompt}";
            shell.Function.Description = $"异步执行Linux sh shell命令，立即返回task_id，用shell_result查询结果。默认超时{ShellManager.DefaultTimeoutSeconds}s，长时间任务建议设置更大值。";
            shell.Function.Parameters.AddRequired("command", new ParameterProperty() { Type = "string", Description = "要执行的命令" });
            shell.Function.Parameters.AddNonRequired("timeout", new ParameterProperty() { Type = "integer", Description = $"超时秒数，默认{ShellManager.DefaultTimeoutSeconds}s" });
            shell.Function.FunctionCall = async (parameters) =>
            {
                var command = parameters["command"].GetString()!;
                var timeout = parameters.TryGetValue("timeout", out var t) ? t.GetInt32() : ShellManager.DefaultTimeoutSeconds;
                var taskId = shellManager.StartCommand(command, timeout);
                return $"任务已启动，task_id: {taskId}。使用 shell_result 工具查询结果。";
            };
            aiClient.RegisterTool(shell);

            var shellSync = new ToolDef();
            shellSync.Function.Name = "shell_sync";
            shellSync.DynamicPrompt = $"同步执行短时命令并直接返回结果。适合ls、cat等快速命令。";
            shellSync.Function.Description = $"同步执行shell命令，等待并返回结果。适用于短时任务（默认{ShellManager.DefaultSyncTimeoutSeconds}s）。";
            shellSync.Function.Parameters.AddRequired("command", new ParameterProperty() { Type = "string", Description = "要执行的命令" });
            shellSync.Function.Parameters.AddNonRequired("timeout", new ParameterProperty() { Type = "integer", Description = $"超时秒数，默认{ShellManager.DefaultSyncTimeoutSeconds}s" });
            shellSync.Function.FunctionCall = async (parameters) =>
            {
                var command = parameters["command"].GetString()!;
                var timeout = parameters.TryGetValue("timeout", out var t) ? t.GetInt32() : ShellManager.DefaultSyncTimeoutSeconds;
                var result = await shellManager.RunSync(command, timeout);
                return PluginUtils.ConstraintLength(result, 3000);
            };
            aiClient.RegisterTool(shellSync);

            var shellResult = new ToolDef();
            shellResult.Function.Name = "shell_result";
            shellResult.DynamicPrompt = "用于查询shell命令的执行结果，需要提供task_id。";
            shellResult.Function.Description = "查询shell命令的执行结果。如果任务仍在执行中会返回提示。";
            shellResult.Function.Parameters.AddRequired("task_id", new ParameterProperty() { Type = "string", Description = "shell命令返回的task_id" });
            shellResult.Function.FunctionCall = async (parameters) =>
            {
                var taskId = parameters["task_id"].GetString()!;
                var (completed, result) = await shellManager.QueryResult(taskId);
                return completed ? PluginUtils.ConstraintLength(result, 3000) : result;
            };
            aiClient.RegisterTool(shellResult);
        }
        var fileSender = RegisterFileSenderTool((filePath) =>
        {
            //file must be in user home directory
            if (!filePath.StartsWith($"/home/{user}/"))
            {
                return (false, $"安全限制：文件路径必须在 /home/{user}/ 目录下，**禁止**泄露其他用户目录或系统文件");
            }
            return (true, string.Empty);
        });
        fileSender.DynamicPrompt = "在发送文件时，禁止泄露其他用户目录或系统文件";
    }

    private string BuildSkillsPrompt(string user)
    {
        var skillsDir = $"/home/{user}/skills";
        if (!System.IO.Directory.Exists(skillsDir))
        {
            return string.Empty;
        }

        try
        {
            var entries = System.IO.Directory.GetFileSystemEntries(skillsDir, "*", System.IO.SearchOption.TopDirectoryOnly);
                
            if (entries.Length == 0) return string.Empty;

            var skillInfos = entries.Select(e => 
            {
                var name = System.IO.Path.GetFileName(e);
                if (string.IsNullOrEmpty(name)) return string.Empty;
                var isDir = System.IO.File.GetAttributes(e).HasFlag(System.IO.FileAttributes.Directory);
                return isDir ? $"[文件夹]{name}" : $"[文件]{name}";
            }).Where(n => !string.IsNullOrEmpty(n));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"~/skills/ 目录下有以下内容（第一层）：{string.Join("、", skillInfos)}。");
            sb.AppendLine("当用户的需求匹配某个技能时，先用 shell_sync 读取技能文件内容（cat ~/skills/<文件名>），或者查看文件夹内容（ls ~/skills/<文件夹名>），按照其中的指令执行。");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RegisterBotForHelp()
    {
        try
        {
            long qq = Interop.GetStructVariable<long>("bot-help")
                ?? throw new Exception("please specific bot-help in variables");
            var solver = new ToolDef();
            solver.Function.Name = "turn_to";
            solver.Function.Description = "让智能AI处理某问题";
            solver.Function.Parameters.AddRequired("question", new ParameterProperty() { Type = "string", Description = "要处理的问题" });
            solver.Function.FunctionCall = async (parameters) =>
            {
                //verify bot in group
                var groupList = await Actions.GetGroupMemberData(parameters.SpecialTag.ToString(), qq.ToString());
                if (groupList == null)
                {
                    return "该工具无法使用，请不要再使用本工具";
                }
                var chain = NapcatClient.Action.Actions.EmptyMessageChain;
                chain.Add(AtData.FromAt(qq.ToString()));
                chain.Add(TextData.FromText($" {parameters["question"]}"));
                await Actions.SendGroupMessage(parameters.SpecialTag, chain);
                return "求助成功，你不用解决这个问题了";
            };
            solver.DynamicPrompt = "如果问题非常复杂，请智能AI求助";
            solver.isUseable = async (tag) =>
            {
                var groupList = await Actions.GetGroupMemberData(tag.ToString(), qq.ToString());
                if (groupList == null)
                {
                    return false;
                }
                return true;
            };
            aiClient.RegisterTool(solver);
            //拦截bot对自己发送的消息
            Interop.Interceptors.Add((data) =>
            {
                return data.sender.user_id == qq;
            });

        }
        catch (Exception e)
        {
            Logger.Warn($"load bot help failed:{e.Message}");
        }
    }
    private void RegisterImagePainter()
    {
        var model = DashscopeModelPreset.QwenImageMax;
        var token_key = model.ApiTokenDictKey;
        var token = Interop.GetClassVariable<string>(token_key);
        if (token == null)
        {
            Logger.Warn($"请在配置文件variable中设置{token_key}");
            return;
        }
        imagePainter = new ImagePainterDashscope(model, token);
        var imagePainterTool = new ToolDef();
        imagePainterTool.Function.Name = "draw_image";
        imagePainterTool.Function.Description = "绘制图片";
        imagePainterTool.Function.Parameters.AddRequired("prompt", new ParameterProperty() { Type = "string", Description = "要绘制的图片描述" });
        imagePainterTool.Function.FunctionCall = async (parameters) =>
        {
            string prompt = parameters["prompt"].GetString()!;
            _ = DrawImageAndSend(prompt, parameters.SpecialTag);
            return "正在绘制中...";
        };
        aiClient.RegisterTool(imagePainterTool);
    }
    private async Task DrawImageAndSend(string prompt, long groupId)
    {
        try
        {
            string url = await imagePainter!.DrawImage(prompt);
            await Actions.SendGroupMessage(groupId,
                [new ImageData { File = url, Summary = prompt }]
                );
        }
        catch (Exception ex)
        {
            Logger.Error($"图片生成异常: {ex.Message}");
            await Actions.SendGroupMessage(groupId, $"图片生成失败，请稍后重试\n{ex.Message}");
        }
    }
    private ToolDef RegisterFileSenderTool(Func<string, (bool isValid, string reason)> validateAccess)
    {
        var fileSender = new ToolDef();
        const int maxSize = 1024 * 1024 * 10; //10MB
        fileSender.Function.Name = "send_file";
        fileSender.Function.Description = "发送文件";
        fileSender.Function.Parameters.AddRequired("path", new ParameterProperty() { Type = "string", Description = "绝对路径" });
        fileSender.Function.FunctionCall = async (parameters) =>
        {
            string filePath = parameters["path"].GetString()!;
            var (isValid, reason) = validateAccess(filePath);
            if (!isValid)
            {
                return $"访问被拒绝: {reason}";
            }
            if (!File.Exists(filePath))
            {
                return $"文件不存在: {filePath}";
            }
            if (new FileInfo(filePath).Length > maxSize)
            {
                return $"文件大小超过{maxSize / 1024 / 1024}MB，无法发送: {filePath}大小为 {new FileInfo(filePath).Length / 1024 / 1024}MB";
            }
            await Actions.SendGroupMessage(parameters.SpecialTag, [FileData.FromFile(filePath)]);
            return "成功";
        };
        aiClient.RegisterTool(fileSender);
        return fileSender;
    }
    private void RegisterMarkdownSender()
    {
        var mdSender=new ToolDef();
        mdSender.Function.Name = "send_markdown";
        mdSender.Function.Description = "支持mermaid、latex公式";
        mdSender.HideOutputOnInvoking = true;
        mdSender.DynamicPrompt="当你需要发送长篇报告或其他类似内容时，请发送markdown。";
        mdSender.Function.Parameters.AddRequired("md", new ParameterProperty() { Type = "string", Description = "需要发送的Markdown文本" });
        mdSender.Function.FunctionCall = async (parameters) =>
        {
            var markdown = parameters["md"];
            byte[] img=await browser.TakeMarkdownScreenshot(markdown.GetString()!);
            await Actions.SendGroupMessage(parameters.SpecialTag, [ImageData.FromBinary(img)]);
            return "done";
        };
        aiClient.RegisterTool(mdSender);
    }
    private void RegisterContextTool(){
        var contextTool=new ToolDef();
        contextTool.Function.Name = "get_context";
        contextTool.Function.Description = "获取上下文";
        contextTool.DynamicPrompt="如果你不能理解用户的问题，请使用工具获取上下文。";
        contextTool.Function.FunctionCall = async (parameters) =>
        {
            string context = await storageManager.GetContext(parameters.SpecialTag);
            return context;
        };
        aiClient.RegisterTool(contextTool);
    }
}
