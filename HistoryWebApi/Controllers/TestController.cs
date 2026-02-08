using Microsoft.AspNetCore.Mvc;

namespace HistoryWebApi.Controllers
{
    [ApiController]
    [Route("/test")]
    public class TestController : ControllerBase
    {
        [HttpGet("")]
        public string Get()
        {
            return "history api is running";
        }
    }
}
