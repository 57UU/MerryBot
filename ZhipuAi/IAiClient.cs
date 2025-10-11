using CommonLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZhipuClient;

namespace ZhipuClient;

public interface IAiClient
{
    /// <summary>
    /// 处理请求
    /// </summary>
    /// <param name="content">询问内容</param>
    /// <param name="id">区分不同对话的id</param>
    /// <param name="sender">发送者</param>
    /// <param name="specialTag">一个tag，该tag会出现在function call的参数中</param>
    /// <returns>异步字符串迭代器，模型返回结果</returns>
    IAsyncEnumerable<string> Ask(string content, long id, string sender, long specialTag = 0);
    
    /// <summary>
    /// 重置对话
    /// </summary>
    /// <param name="id">对话id</param>
    void Reset(long id);
    
    /// <summary>
    /// 注册工具
    /// </summary>
    /// <param name="tool">工具定义</param>
    void RegisterTool(ToolDef tool);
    
    /// <summary>
    /// 获取对话历史
    /// </summary>
    /// <param name="uid">用户id</param>
    /// <returns>对话历史</returns>
    System.ReadOnlySpan<ZhipuMessage> GetDialogHistory(long uid);
    
    /// <summary>
    /// 是否使用动态提示词
    /// </summary>
    bool UseDynamicPrompt { get; set; }
    
    /// <summary>
    /// 日志记录器
    /// </summary>
    ISimpleLogger Logger { set; }
}