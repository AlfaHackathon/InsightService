using Microsoft.AspNetCore.Mvc;
namespace InsightService.Controllers;

[ApiController]
public sealed class HealthController : ApiControllerBase
{
    [HttpGet("health")]
    public IActionResult Get() => Ok(new { service = "insight-service", status = "ok" });
}
