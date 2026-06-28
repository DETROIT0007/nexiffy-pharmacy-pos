using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Get() =>
            Ok(new { status = "ok", time = DateTime.UtcNow });
    }
}
