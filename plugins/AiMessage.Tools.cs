using NapcatClient.MessageType;
using System.Runtime.InteropServices;
using ZhipuClient;


namespace BotPlugin;

public partial class AiMessage
{
    void RegisterVoiceTool()
    {
        var voiceSender = new ToolDef();
        voiceSender.Function.Name = "send_voice";
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
        const string user = "merrybot";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var terminal = Terminal.CreateUserTerminal(user);
            int timeout = 10;
            var shell = new ToolDef();
            shell.Function.Name = "shell";
            shell.DynamicPrompt = $"你可以使用shell工具在你的linux电脑上执行命令，已安装py等程序，user: {user}";
            shell.Function.Description = $"执行Linux sh shell命令.(限时{timeout}s)";
            shell.Function.Parameters.AddRequired("command", new ParameterProperty() { Type = "string", Description = "要执行的命令" });
            shell.Function.FunctionCall = async (parameters) =>
            {
                var result = await terminal.RunCommandAsync(
                    parameters["command"].GetString()!,
                    timeoutMs: timeout * 1000 + 500,
                    useHardTimeout: true,
                    waitMutex: true
                    );
                return PluginUtils.ConstraintLength(result, 3000);
            };
            aiClient.RegisterTool(shell);
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
        mdSender.Function.Description = "";
        mdSender.DynamicPrompt="当你需要发送长篇报告时，请发送markdown";
        mdSender.Function.Parameters.AddRequired("md", new ParameterProperty() { Type = "string", Description = "需要发送的Markdown文本" });
        mdSender.Function.FunctionCall = async (parameters) =>
        {
            var markdown = parameters["md"];
            byte[] img=await aiClient.browser.TakeMarkdownScreenshot(markdown.GetString()!);
            await Actions.SendGroupMessage(parameters.SpecialTag, [ImageData.FromBinary(img)]);
            return "done";
        };
        aiClient.RegisterTool(mdSender);
    }
}
