using AlfaHackaton.Controllers.Controllers;
using Microsoft.AspNetCore.Mvc;
namespace InsightService.Controllers;

[ApiController]
public sealed class HealthController : BaseController
{
    [HttpGet("health")]
    public IActionResult Get() => Ok(new { service = "insight-service", status = "ok" });
}
