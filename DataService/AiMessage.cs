using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataService;


public class AiMessage
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public string MessageType { get; set; }
    public string Content { get; set; }
    public long Time { get; set; }
}
