using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataService;

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。


public class AiMessage
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public string MessageType { get; set; }
    public string Content { get; set; }
    public long Time { get; set; }
}

#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。