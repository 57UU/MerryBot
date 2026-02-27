using CommonLib;
using NapcatClient;
using NapcatClient.MessageType;
using OpenQA.Selenium;
using OpenQA.Selenium.BiDi.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BotPlugin;

[PluginTag("shell", "使用 /sh 运行终端命令")]
public class RunCommand : Plugin
{
    long authorized;
    bool useUnprivileged = true;
    public RunCommand(PluginInterop interop) : base(interop)
    {
        //not Linux 
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PluginNotUsableException("shell plugin can only support Linux");
        }
        terminal = Terminal.CreateUserTerminal();
        terminal.logger = Logger;
        authorized=interop.AuthorizedUser;
        Logger.Info("shell plugin started");
    }
    public override void OnGroupMessageMentioned(long groupId, MessageChain chain, ReceivedGroupMessage data)
    {
        long sender=data.sender.user_id;
        var isAuthorized = sender == authorized;
        if (useUnprivileged==false && !isAuthorized)
        {
            _ = Actions.SendGroupMessage(groupId, "401 Unauthorized\nYou do not have the permission");
            return;
        }
        if (IsStartsWith(chain, "/sh"))
        {
            var text = (chain[0] as TextData)!.Text.Trim();
            //rm first /sh
            var first = text.IndexOf(' ');
            if (first == -1)
            {
                _ = Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            text = text[first..];
            if (text.Length == 0)
            {
                _ = Actions.SendGroupMessage(groupId, "请输入命令");
                return;
            }
            if (text[0] == ' ')
            {
                text = text[1..];
            }
            _=HandleCommand(text, groupId,data.message_id,isAuthorized);
        }
    }
    internal Terminal terminal ;
    async Task HandleCommand(string command,long groupId,long messageId,bool isAuthorized=false)
    {
        string result;
        try
        {
            result = await terminal.RunCommandAsync(command, timeoutMs: 3000,useHardTimeout:true);
        }
        catch (Exception e) { 
            result = $"error:{e.Message}";
        }
        
        result = PluginUtils.ConstraintLength(result, 3000);

        await Actions.ChooseBestReplyMethod(groupId, messageId, result);
    }

}


